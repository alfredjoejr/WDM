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
    private static readonly object IpcLock = new();

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
        var engine = new DownloadEngine();
        _activeDownloads[downloadId] = engine;

        var startEvent = new
        {
            requestId,
            action = "download_started",
            downloadId,
            url,
            fileName = customFileName
        };

        // Acknowledge start immediately to browser and UI
        await HostProtocol.SendMessageAsync(startEvent, ct);
        BroadcastToUi(startEvent);

        // Run download in background and stream progress back
        _ = Task.Run(async () =>
        {
            try
            {
                engine.ProgressChanged += async snapshot =>
                {
                    var progressEvent = new
                    {
                        action = "download_progress",
                        downloadId,
                        percentage = snapshot.ProgressPercentage,
                        speed = snapshot.SpeedFormatted,
                        speedBytes = snapshot.SpeedBytesPerSecond,
                        downloaded = snapshot.DownloadedFormatted,
                        total = snapshot.TotalFormatted,
                        eta = snapshot.EstimatedRemainingTime.HasValue ? $"{snapshot.EstimatedRemainingTime.Value:mm\\:ss}" : "--:--",
                        activeConnections = snapshot.ActiveConnections
                    };
                    await HostProtocol.SendMessageAsync(progressEvent);
                    BroadcastToUi(progressEvent);
                };

                var options = new DownloadOptions
                {
                    Url = url,
                    CustomFileName = customFileName,
                    Connections = connections,
                    CustomHeaders = headers
                };

                var savedPath = await engine.StartAsync(options);
                var hash = await ChecksumVerifier.ComputeHashAsync(savedPath);

                var completedEvent = new
                {
                    action = "download_completed",
                    downloadId,
                    filePath = savedPath,
                    sha256 = hash
                };
                await HostProtocol.SendMessageAsync(completedEvent);
                BroadcastToUi(completedEvent);
            }
            catch (Exception ex)
            {
                var failedEvent = new
                {
                    action = "download_failed",
                    downloadId,
                    error = ex.Message
                };
                await HostProtocol.SendMessageAsync(failedEvent);
                BroadcastToUi(failedEvent);
            }
            finally
            {
                _activeDownloads.TryRemove(downloadId, out _);
            }
        }, ct);
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

        var startEvent = new
        {
            requestId,
            action = "media_download_started",
            downloadId,
            url,
            fileName = fileName ?? "Media Video.mp4",
            formatId,
            isAudioOnly,
            destinationDirectory
        };

        await HostProtocol.SendMessageAsync(startEvent, ct);
        BroadcastToUi(startEvent);

        _ = Task.Run(async () =>
        {
            try
            {
                var completedPath = await _mediaExtractor.DownloadMediaAsync(
                    url,
                    formatId,
                    destinationDirectory,
                    fileName,
                    isAudioOnly,
                    progressCallback: async (percent, line) =>
                    {
                        var progressEvent = new
                        {
                            action = "media_download_progress",
                            downloadId,
                            percentage = percent,
                            statusText = line,
                            status = line
                        };
                        await HostProtocol.SendMessageAsync(progressEvent);
                        BroadcastToUi(progressEvent);
                    },
                    cancellationToken: ct);

                var completedEvent = new
                {
                    action = "media_download_completed",
                    downloadId,
                    filePath = completedPath,
                    destinationDirectory = Path.GetDirectoryName(completedPath) ?? targetDirOrNull(completedPath)
                };
                await HostProtocol.SendMessageAsync(completedEvent);
                BroadcastToUi(completedEvent);
            }
            catch (Exception ex)
            {
                var failedEvent = new
                {
                    action = "media_download_failed",
                    downloadId,
                    error = ex.Message
                };
                await HostProtocol.SendMessageAsync(failedEvent);
                BroadcastToUi(failedEvent);
            }
        }, ct);
    }

    private static string targetDirOrNull(string path) => Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? string.Empty);

    private static (bool Success, string? Message) HandleOpenApp(JsonElement root)
    {
        try
        {
            var targetUrl = root.TryGetProperty("url", out var uProp) && uProp.ValueKind == JsonValueKind.String
                ? uProp.GetString()
                : null;

            BroadcastToUi(new
            {
                action = "bring_to_front",
                url = targetUrl,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            // 1. Check if WDM.UI is already running with a valid window
            var existingProcesses = Process.GetProcessesByName("WDM.UI");
            bool activatedExisting = false;
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
                        activatedExisting = true;
                    }
                }
                catch { }
            }

            if (activatedExisting)
            {
                Program.Log("HandleOpenApp: Signaled existing WDM.UI instance to come to foreground.");
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

            // Read host_config.json if available
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
                        Program.Log($"HandleOpenApp: Starting UI process: {full}");
                        var psi = new ProcessStartInfo
                        {
                            FileName = full,
                            Arguments = !string.IsNullOrWhiteSpace(targetUrl) ? $"\"{targetUrl}\"" : string.Empty,
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Normal,
                            WorkingDirectory = Path.GetDirectoryName(full)
                        };
                        Process.Start(psi);
                        return (true, null);
                    }
                }
                catch (Exception innerEx)
                {
                    Program.Log($"HandleOpenApp candidate failed ({path}): {innerEx.Message}");
                }
            }

            var msg = "Failed to locate WDM.UI.exe. Please run install-host.bat to deploy WDM.";
            Program.Log($"HandleOpenApp: {msg}");
            return (false, msg);
        }
        catch (Exception ex)
        {
            Program.Log($"HandleOpenApp Exception: {ex.Message}");
            return (false, ex.Message);
        }
    }
}
