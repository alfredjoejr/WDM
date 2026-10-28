using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WDM.Core.Engine;
using WDM.Core.Media;
using WDM.Core.Models;
using WDM.Core.Network;
using WDM.Core.Security;

namespace WDM.NativeHost;

/// <summary>
/// Handles JSON-RPC actions dispatched from the browser extension.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class HostService
{
    private readonly HttpProbe _probe = new();
    private readonly MediaExtractor _mediaExtractor = new();
    private readonly ConcurrentDictionary<string, DownloadEngine> _activeDownloads = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeMediaDownloads = new();
    private static readonly object IpcLock = new();
    private static readonly object UiLaunchLock = new();
    private static DateTime _lastUiLaunchUtc = DateTime.MinValue;

    public HostService()
    {
        StartIpcWatcher();
    }

    private void StartIpcWatcher()
    {
        _ = Task.Run(async () =>
        {
            long lastPos = 0;
            if (File.Exists(IpcEventsFilePath))
            {
                try { lastPos = new FileInfo(IpcEventsFilePath).Length; } catch { }
            }

            while (true)
            {
                try
                {
                    await Task.Delay(400);
                    if (!File.Exists(IpcEventsFilePath)) continue;

                    using var fs = new FileStream(IpcEventsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < lastPos) lastPos = 0;
                    if (fs.Length == lastPos) continue;

                    fs.Seek(lastPos, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            var action = root.TryGetProperty("action", out var aProp) ? aProp.GetString() : null;
                            if (action == "cancel_download" || action == "pause_download")
                            {
                                HandleCancelDownload(root);
                            }
                        }
                        catch { }
                    }
                    lastPos = fs.Position;
                }
                catch { }
            }
        });
    }

    private static string IpcEventsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM", "extension_events.jsonl");

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    public static void BroadcastToUi(object payload)
    {
        try
        {
            var filePath = IpcEventsFilePath;
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            lock (IpcLock)
            {
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"BroadcastToUi error: {ex.Message}");
        }
    }

    public async Task HandleMessageAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : null;
        var requestId = (root.TryGetProperty("requestId", out var reqProp) ? reqProp.GetString() : null) 
                        ?? Guid.NewGuid().ToString("N");

        try
        {
            switch (action)
            {
                case "ping":
                    await HostProtocol.SendMessageAsync(new
                    {
                        requestId,
                        status = "pong",
                        version = "1.0.0",
                        engine = ".NET 10"
                    }, cancellationToken);
                    break;

                case "probe":
                    await HandleProbeAsync(root, requestId, cancellationToken);
                    break;

                case "start_download":
                    await HandleStartDownloadAsync(root, requestId, cancellationToken);
                    break;

                case "sniff_media":
                    await HandleSniffMediaAsync(root, requestId, cancellationToken);
                    break;

                case "download_media":
                    await HandleDownloadMediaAsync(root, requestId, cancellationToken);
                    break;

                case "cancel_download":
                    HandleCancelDownload(root);
                    await HostProtocol.SendMessageAsync(new { requestId, success = true }, cancellationToken);
                    break;

                case "pause_download":
                    HandleCancelDownload(root);
                    await HostProtocol.SendMessageAsync(new { requestId, success = true }, cancellationToken);
                    break;

                case "open_app":
                    var (opened, errorMsg) = HandleOpenApp(root);
                    await HostProtocol.SendMessageAsync(new
                    {
                        requestId,
                        success = opened,
                        message = errorMsg
                    }, cancellationToken);
                    break;

                default:
                    await HostProtocol.SendMessageAsync(new
                    {
                        requestId,
                        error = $"Unknown action '{action}'"
                    }, cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            await HostProtocol.SendMessageAsync(new
            {
                requestId,
                error = ex.Message
            }, cancellationToken);
        }
    }

    private async Task HandleProbeAsync(JsonElement root, string requestId, CancellationToken ct)
    {
        var url = root.GetProperty("url").GetString()!;
        var meta = await _probe.ProbeAsync(url, cancellationToken: ct);

        await HostProtocol.SendMessageAsync(new
        {
            requestId,
            action = "probe_result",
            data = new
            {
                fileName = meta.FileName,
                totalBytes = meta.TotalBytes,
                acceptsRanges = meta.AcceptsRanges,
                contentType = meta.ContentType,
                etag = meta.ETag,
                httpVersion = meta.HttpVersion?.ToString(),
                finalUrl = meta.FinalUrl
            }
        }, ct);
    }

    private async Task HandleStartDownloadAsync(JsonElement root, string requestId, CancellationToken ct)
    {
        var url = root.GetProperty("url").GetString()!;
        var customFileName = root.TryGetProperty("fileName", out var fProp) ? fProp.GetString() : null;
        var connections = root.TryGetProperty("connections", out var cProp) ? cProp.GetInt32() : 16;

        Dictionary<string, string>? headers = null;
        if (root.TryGetProperty("headers", out var hProp) && hProp.ValueKind == JsonValueKind.Object)
        {
            headers = new();
            foreach (var prop in hProp.EnumerateObject())
            {
                headers[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        var downloadId = Guid.NewGuid().ToString("N");

        // Ensure WDM.UI is running (outside the browser's Job Object) and delegate the download to WDM.UI
        // so that closing the browser never interrupts active downloads or kills WDM.UI.
        EnsureUiRunning();

        var ipcCommand = new
        {
            requestId,
            action = "ipc_start_download",
            downloadId,
            url,
            fileName = customFileName,
            connections,
            headers,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        BroadcastToUi(ipcCommand);

        var startAck = new
        {
            requestId,
            action = "download_started",
            downloadId,
            url,
            fileName = customFileName,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await HostProtocol.SendMessageAsync(startAck, ct);
    }

    private async Task HandleSniffMediaAsync(JsonElement root, string requestId, CancellationToken ct)
    {
        var url = root.GetProperty("url").GetString()!;
        Program.Log($"HandleSniffMediaAsync: Sniffing streams for {url}");
        var metadata = await _mediaExtractor.ExtractMetadataAsync(url, ct);

        if (metadata == null)
        {
            Program.Log($"HandleSniffMediaAsync: No streams extracted for {url}");
            await HostProtocol.SendMessageAsync(new
            {
                requestId,
                action = "sniff_media_result",
                success = false,
                message = "No streams could be extracted or yt-dlp is missing"
            }, ct);
            return;
        }

        Program.Log($"HandleSniffMediaAsync: Successfully extracted {metadata.Formats.Count} formats for '{metadata.Title}'");
        await HostProtocol.SendMessageAsync(new
        {
            requestId,
            action = "sniff_media_result",
            success = true,
            data = new
            {
                title = metadata.Title,
                uploader = metadata.Uploader,
                durationSeconds = metadata.Duration?.TotalSeconds,
                thumbnail = metadata.ThumbnailUrl,
                formats = metadata.Formats.Select(f => new
                {
                    formatId = f.FormatId,
                    resolution = f.Resolution,
                    extension = f.Extension,
                    fileSizeBytes = f.FileSizeBytes,
                    note = f.Note,
                    isAudioOnly = f.IsAudioOnly
                })
            }
        }, ct);
    }

    private async Task HandleDownloadMediaAsync(JsonElement root, string requestId, CancellationToken ct)
    {
        var url = root.GetProperty("url").GetString()!;
        var formatId = root.TryGetProperty("formatId", out var fProp) ? fProp.GetString() : null;
        var fileName = root.TryGetProperty("fileName", out var fnProp) ? fnProp.GetString() : null;
        var isAudioOnly = root.TryGetProperty("isAudioOnly", out var aoProp) && aoProp.GetBoolean();
        var destinationDirectory = root.TryGetProperty("destinationDirectory", out var dProp) ? dProp.GetString() : null;

        var downloadId = Guid.NewGuid().ToString("N");

        // Ensure WDM.UI is running (outside the browser's Job Object) and delegate the media download to WDM.UI
        // so that closing the browser never terminates yt-dlp or WDM.UI.
        EnsureUiRunning();

        var ipcCommand = new
        {
            requestId,
            action = "ipc_download_media",
            downloadId,
            url,
            fileName = fileName ?? "Media Video.mp4",
            formatId,
            isAudioOnly,
            destinationDirectory,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        BroadcastToUi(ipcCommand);

        var startAck = new
        {
            requestId,
            action = "media_download_started",
            downloadId,
            url,
            fileName = fileName ?? "Media Video.mp4",
            formatId,
            isAudioOnly,
            destinationDirectory,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await HostProtocol.SendMessageAsync(startAck, ct);
    }

    public void HandleCancelDownload(JsonElement root)
    {
        var downloadId = root.TryGetProperty("downloadId", out var idProp) ? idProp.GetString() : null;
        if (!string.IsNullOrWhiteSpace(downloadId))
        {
            Program.Log($"HandleCancelDownload: Canceling download {downloadId}");
            if (_activeDownloads.TryRemove(downloadId, out var engine))
            {
                engine.Cancel();
            }
            if (_activeMediaDownloads.TryRemove(downloadId, out var cts))
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch { }
            }
        }
    }

    private static string targetDirOrNull(string path) => Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? string.Empty);

    private static (bool Success, string? Message) EnsureUiRunning(string? targetUrl = null, bool broadcastBringToFront = false)
    {
        try
        {
            if (broadcastBringToFront)
            {
                BroadcastToUi(new
                {
                    action = "bring_to_front",
                    url = targetUrl,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }

            lock (UiLaunchLock)
            {
                // 1. Check if WDM.UI is already running
                var existingProcesses = Process.GetProcessesByName("WDM.UI");
                if (existingProcesses.Length > 0)
                {
                    foreach (var proc in existingProcesses)
                    {
                        try
                        {
                            proc.Refresh();
                            if (proc.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(proc.MainWindowHandle, IsIconic(proc.MainWindowHandle) ? SW_RESTORE : SW_SHOW);
                                SwitchToThisWindow(proc.MainWindowHandle, true);
                                SetForegroundWindow(proc.MainWindowHandle);
                            }
                        }
                        catch { }
                    }

                    Program.Log("EnsureUiRunning: Signaled existing WDM.UI instance to come to foreground.");
                    return (true, null);
                }

                // Debounce rapid consecutive launch requests (e.g., download_media + open_app arriving 1ms apart)
                if ((DateTime.UtcNow - _lastUiLaunchUtc).TotalSeconds < 4)
                {
                    Program.Log("EnsureUiRunning: UI launch already in progress (debounced).");
                    return (true, null);
                }

                // 2. Locate WDM.UI.exe across LocalAppData, host_config.json, and workspace directories
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var wdmAppData = Path.Combine(localAppData, "WDM");
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                var candidates = new List<string>
                {
                    Path.Combine(wdmAppData, "UI", "WDM.UI.exe"),
                    Path.Combine(wdmAppData, "WDM.UI", "WDM.UI.exe"),
                    Path.Combine(wdmAppData, "WDM.UI.exe")
                };

                var configPath = Path.Combine(wdmAppData, "host_config.json");
                if (File.Exists(configPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                        if (doc.RootElement.TryGetProperty("installedUiExePath", out var p1) && p1.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p1.GetString()))
                        {
                            candidates.Add(p1.GetString()!);
                        }
                        if (doc.RootElement.TryGetProperty("sourceUiExePath", out var p2) && p2.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p2.GetString()))
                        {
                            candidates.Add(p2.GetString()!);
                        }
                    }
                    catch { }
                }

                candidates.AddRange([
                    Path.Combine(baseDir, "..", "WDM.UI", "WDM.UI.exe"),
                    Path.Combine(baseDir, "..", "UI", "WDM.UI.exe"),
                    Path.Combine(baseDir, "WDM.UI.exe"),
                    Path.Combine(baseDir, "..", "WDM.UI.exe"),
                    Path.Combine(baseDir, "..", "..", "bin", "WDM.UI", "WDM.UI.exe"),
                    Path.Combine(baseDir, "..", "..", "dist", "WDM-v1.0.0-windows-x64", "WDM.UI.exe"),
                    Path.Combine(baseDir, "..", "..", "src", "WDM.UI", "bin", "Release", "net10.0-windows", "win-x64", "WDM.UI.exe"),
                    Path.Combine(baseDir, "..", "..", "src", "WDM.UI", "bin", "Release", "net10.0-windows", "WDM.UI.exe"),
                    Path.Combine(baseDir, "..", "..", "src", "WDM.UI", "bin", "Debug", "net10.0-windows", "WDM.UI.exe")
                ]);

                foreach (var path in candidates)
                {
                    try
                    {
                        var full = Path.GetFullPath(path);
                        if (File.Exists(full))
                        {
                            Program.Log($"EnsureUiRunning: Launching UI detached from browser Job Object: {full}");
                            _lastUiLaunchUtc = DateTime.UtcNow;
                            LaunchDetachedFromBrowserJob(full);
                            return (true, null);
                        }
                    }
                    catch (Exception innerEx)
                    {
                        Program.Log($"EnsureUiRunning candidate failed ({path}): {innerEx.Message}");
                    }
                }

                var msg = "Failed to locate WDM.UI.exe. Please run setup.bat to deploy WDM.";
                Program.Log($"EnsureUiRunning: {msg}");
                return (false, msg);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"EnsureUiRunning Exception: {ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Launches WDM.UI.exe completely outside the browser's NativeMessagingHost Job Object
    /// (which sets JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE).
    /// 1. First attempts Win32 CreateProcessW with CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS (bInheritHandles = false).
    /// 2. If the browser's Job Object blocks direct breakaway, spawns via WMI Win32_Process.Create (parented by WmiPrvSE.exe service).
    /// This guarantees WDM.UI stays open even when Firefox, Chrome, or Edge is closed.
    /// </summary>
    private static void LaunchDetachedFromBrowserJob(string fullExePath)
    {
        var workDir = Path.GetDirectoryName(fullExePath) ?? AppDomain.CurrentDomain.BaseDirectory;

        // Tier 1: Win32 CreateProcessW with CREATE_BREAKAWAY_FROM_JOB (instant, zero overhead)
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            const uint flags = CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS;
            var cmdLine = $"\"{fullExePath}\"";

            if (CreateProcessW(fullExePath, cmdLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workDir, ref si, out var pi))
            {
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                Program.Log($"LaunchDetachedFromBrowserJob: Started via CREATE_BREAKAWAY_FROM_JOB (PID {pi.dwProcessId}).");
                return;
            }
            Program.Log($"LaunchDetachedFromBrowserJob: CREATE_BREAKAWAY_FROM_JOB denied (Win32 error {Marshal.GetLastWin32Error()}), falling back to WMI service.");
        }
        catch (Exception ex)
        {
            Program.Log($"LaunchDetachedFromBrowserJob Tier 1 exception: {ex.Message}");
        }

        // Tier 2: WMI Win32_Process.Create (spawned by Windows WmiPrvSE.exe service, 100% outside browser Job Object)
        try
        {
            var escapedExe = fullExePath.Replace("'", "''");
            var escapedDir = workDir.Replace("'", "''");
            var psCmd = $"Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{{CommandLine='\"{escapedExe}\"'; CurrentDirectory='{escapedDir}'}} | Out-Null";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{psCmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };
            Process.Start(psi);
            Program.Log("LaunchDetachedFromBrowserJob: Started via WMI Win32_Process.Create.");
            return;
        }
        catch (Exception ex)
        {
            Program.Log($"LaunchDetachedFromBrowserJob Tier 2 exception ({ex.Message}), using explorer fallback.");
        }

        // Tier 3: Explorer fallback
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullExePath,
                UseShellExecute = true,
                WorkingDirectory = workDir
            });
        }
        catch { }
    }

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint DETACHED_PROCESS = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private static (bool Success, string? Message) HandleOpenApp(JsonElement root)
    {
        var targetUrl = root.TryGetProperty("url", out var uProp) && uProp.ValueKind == JsonValueKind.String
            ? uProp.GetString()
            : null;

        return EnsureUiRunning(targetUrl, broadcastBringToFront: true);
    }
}
