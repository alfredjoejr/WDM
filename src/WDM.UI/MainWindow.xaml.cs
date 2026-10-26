using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WDM.Core.Engine;
using WDM.Core.Media;
using WDM.Core.Models;
using WDM.Core.Network;
using WDM.Core.Security;

namespace WDM.UI;

public partial class MainWindow : Window
{
    private readonly HttpProbe _probe = new();
    private readonly MediaExtractor _mediaExtractor = new();
    private readonly ConcurrentDictionary<string, DownloadEngine> _activeEngines = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeMediaDownloads = new();
    private readonly string _historyFilePath;
    private readonly string _ipcEventsFilePath;
    private long _ipcLastPosition;
    private bool _isInitialized;
    private bool _isWebLoaded;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    public MainWindow()
    {
        InitializeComponent();
        
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
        Directory.CreateDirectory(appData);
        _historyFilePath = Path.Combine(appData, "downloads_history.json");
        _ipcEventsFilePath = Path.Combine(appData, "extension_events.jsonl");

        // Start tailing from the recent 16KB so if WDM.UI was just launched by the browser extension,
        // it immediately catches the media_download_started event written milliseconds earlier.
        // Stale events (>15s old or missing timestamps) are filtered out in StartExtensionIpcTailer.
        if (File.Exists(_ipcEventsFilePath))
        {
            try
            {
                var len = new FileInfo(_ipcEventsFilePath).Length;
                _ipcLastPosition = Math.Max(0, len - 16384);
            }
            catch { }
        }

        Loaded += OnWindowLoaded;
    }

    private void BringToFront()
    {
        try
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();

            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SwitchToThisWindow(handle, true);
                SetForegroundWindow(handle);
            }
        }
        catch { }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var webViewDataDir = Path.Combine(localAppData, "WDM", "WebView2Data");
            Directory.CreateDirectory(webViewDataDir);

            // Clean up old temporary session folders from previous runs in the background
            _ = Task.Run(() => CleanupOldSessionFolders(localAppData));

            CoreWebView2Environment env;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(null, webViewDataDir);
                await WebView.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                // If primary folder is locked by another instance or hung process, seamlessly fall back to an isolated session directory
                System.Diagnostics.Debug.WriteLine($"[WebView2 primary folder locked (0x800700AA), falling back to isolated session]: {ex.Message}");
                var fallbackDir = Path.Combine(localAppData, "WDM", $"WebView2_Session_{Environment.ProcessId}");
                Directory.CreateDirectory(fallbackDir);
                env = await CoreWebView2Environment.CreateAsync(null, fallbackDir);
                await WebView.EnsureCoreWebView2Async(env);
            }

            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // Automatically grant clipboard read permissions so web code can read clipboard seamlessly
            WebView.CoreWebView2.PermissionRequested += (s, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                {
                    args.State = CoreWebView2PermissionState.Allow;
                }
            };

            // Open external URLs and mailto links in the user's default desktop browser
            WebView.CoreWebView2.NavigationStarting += (s, args) =>
            {
                if (args.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    args.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    args.Uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = args.Uri,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            };

            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            WebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                if (_isWebLoaded) return;
                _isWebLoaded = true;

                // Handle command-line URL if WDM.UI was launched with a URL argument
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) &&
                    (args[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     args[1].StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    PostToWeb(new { action = "open_url", url = args[1] });
                }

                StartExtensionIpcTailer();
            };

            BringToFront();

            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
            if (File.Exists(htmlPath))
            {
                WebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                WebView.CoreWebView2.NavigateToString("<h1 style='color:white;background:#0A0F1D;padding:40px;'>WDM UI: index.html not found</h1>");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize WebView2: {ex.Message}", "WDM Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartExtensionIpcTailer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };

        timer.Tick += (_, _) =>
        {
            try
            {
                if (!File.Exists(_ipcEventsFilePath)) return;

                using var fs = new FileStream(_ipcEventsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < _ipcLastPosition)
                {
                    _ipcLastPosition = 0;
                }
                if (fs.Length == _ipcLastPosition) return;

                fs.Seek(_ipcLastPosition, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string? line;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        // Drop stale events from previous sessions (older than 15s or missing timestamp)
                        if (!root.TryGetProperty("timestamp", out var tsProp) || !tsProp.TryGetInt64(out var ts) || (now - ts > 15000))
                        {
                            continue;
                        }

                        var action = root.TryGetProperty("action", out var aProp) ? aProp.GetString() : null;

                        if (action == "bring_to_front")
                        {
                            BringToFront();
                            if (root.TryGetProperty("url", out var uProp) && uProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(uProp.GetString()))
                            {
                                PostToWeb(new { action = "open_url", url = uProp.GetString() });
                            }
                            else
                            {
                                PostToWeb(new { action = "switch_tab", tab = "downloads" });
                            }
                        }
                        else if (action == "cancel_download" || action == "pause_download")
                        {
                            var cancelId = root.TryGetProperty("downloadId", out var idProp) ? idProp.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(cancelId))
                            {
                                if (_activeEngines.TryRemove(cancelId, out var engine)) engine.Cancel();
                                if (_activeMediaDownloads.TryRemove(cancelId, out var cts)) { try { cts.Cancel(); cts.Dispose(); } catch { } }
                            }
                            WebView?.CoreWebView2?.PostWebMessageAsJson(line);
                        }
                        else if (!string.IsNullOrWhiteSpace(action))
                        {
                            WebView?.CoreWebView2?.PostWebMessageAsJson(line);
                        }
                    }
                    catch { }
                }

                _ipcLastPosition = fs.Position;
            }
            catch { }
        };

        timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            WebView?.Dispose();
        }
        catch { }
        Environment.Exit(0);
    }

    private static void CleanupOldSessionFolders(string localAppData)
    {
        try
        {
            var wdmDir = Path.Combine(localAppData, "WDM");
            if (!Directory.Exists(wdmDir)) return;

            var dirs = Directory.GetDirectories(wdmDir, "WebView2_Session_*");
            foreach (var d in dirs)
            {
                try
                {
                    Directory.Delete(d, true);
                }
                catch { }
            }
        }
        catch { }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var action = root.TryGetProperty("action", out var aProp) ? aProp.GetString() : null;
            var requestId = root.TryGetProperty("requestId", out var rProp) ? rProp.GetString() : Guid.NewGuid().ToString("N");

            switch (action)
            {
                case "probe_url":
                    await HandleProbeUrlAsync(root, requestId);
                    break;

                case "start_download":
                    await HandleStartDownloadAsync(root, requestId);
                    break;

                case "pause_download":
                    HandlePauseDownload(root);
                    break;

                case "cancel_download":
                    HandleCancelDownload(root);
                    break;

                case "delete_download_files":
                    HandleDeleteDownloadFiles(root);
                    break;

                case "open_file":
                    HandleOpenFile(root);
                    break;

                case "open_folder":
                    HandleOpenFolder(root);
                    break;

                case "sniff_media":
                    await HandleSniffMediaAsync(root, requestId);
                    break;

                case "download_media":
                    await HandleDownloadMediaAsync(root, requestId);
                    break;

                case "browse_folder":
                    HandleBrowseFolder(requestId);
                    break;

                case "get_default_folder":
                    HandleGetDefaultFolder(requestId);
                    break;

                case "get_clipboard":
                    HandleGetClipboard(requestId);
                    break;

                case "load_history":
                    await HandleLoadHistoryAsync(requestId);
                    break;

                case "save_history":
                    await HandleSaveHistoryAsync(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            PostToWeb(new { action = "error", error = ex.Message });
        }
    }

    private async Task HandleProbeUrlAsync(JsonElement root, string? requestId)
    {
        var url = root.GetProperty("url").GetString()!;
        try
        {
            var meta = await _probe.ProbeAsync(url);
            PostToWeb(new
            {
                requestId,
                action = "probe_result",
                success = true,
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
            });
        }
        catch (Exception ex)
        {
            PostToWeb(new { requestId, action = "probe_result", success = false, message = ex.Message });
        }
    }

    private async Task HandleStartDownloadAsync(JsonElement root, string? requestId)
    {
        var url = root.GetProperty("url").GetString()!;
        var fileName = root.TryGetProperty("fileName", out var fProp) ? fProp.GetString() : null;
        var connections = root.TryGetProperty("connections", out var cProp) ? cProp.GetInt32() : 16;
        var outDir = root.TryGetProperty("destinationDirectory", out var dProp) ? dProp.GetString() : null;
        var speedLimit = root.TryGetProperty("speedLimit", out var lProp) && lProp.GetInt64() > 0 ? lProp.GetInt64() : (long?)null;

        var downloadId = (root.TryGetProperty("downloadId", out var idProp) ? idProp.GetString() : null) ?? Guid.NewGuid().ToString("N");
        var engine = new DownloadEngine();
        _activeEngines[downloadId] = engine;

        PostToWeb(new
        {
            requestId,
            action = "download_started",
            downloadId,
            url,
            fileName
        });

        _ = Task.Run(async () =>
        {
            try
            {
                engine.ProgressChanged += snapshot =>
                {
                    PostToWeb(new
                    {
                        action = "download_progress",
                        downloadId,
                        percentage = snapshot.ProgressPercentage,
                        speed = snapshot.SpeedFormatted,
                        speedBytes = snapshot.SpeedBytesPerSecond,
                        downloaded = snapshot.DownloadedFormatted,
                        total = snapshot.TotalFormatted,
                        eta = snapshot.EstimatedRemainingTime.HasValue ? $"{snapshot.EstimatedRemainingTime.Value:mm\\:ss}" : "--:--",
                        activeConnections = snapshot.ActiveConnections,
                        chunks = snapshot.Chunks.Select(c => new
                        {
                            index = c.Index,
                            start = c.StartOffset,
                            end = c.EndOffset,
                            bytes = c.BytesDownloaded,
                            total = c.TotalBytes,
                            progress = c.ProgressPercentage,
                            status = c.Status.ToString()
                        })
                    });
                };

                var options = new DownloadOptions
                {
                    Url = url,
                    CustomFileName = fileName,
                    DestinationDirectory = outDir,
                    Connections = connections,
                    MaxBytesPerSecond = speedLimit
                };

                var savedPath = await engine.StartAsync(options);
                var sha256 = await ChecksumVerifier.ComputeHashAsync(savedPath);

                PostToWeb(new
                {
                    action = "download_completed",
                    downloadId,
                    filePath = savedPath,
                    sha256
                });
            }
            catch (Exception ex)
            {
                PostToWeb(new
                {
                    action = "download_failed",
                    downloadId,
                    error = ex.Message
                });
            }
            finally
            {
                _activeEngines.TryRemove(downloadId, out _);
            }
        });

        await Task.CompletedTask;
    }

    private void HandlePauseDownload(JsonElement root)
    {
        var id = root.GetProperty("downloadId").GetString()!;
        if (_activeEngines.TryGetValue(id, out var engine))
        {
            engine.Pause();
        }
        if (_activeMediaDownloads.TryRemove(id, out var cts))
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        BroadcastIpcEvent(new { action = "pause_download", downloadId = id, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    private void HandleCancelDownload(JsonElement root)
    {
        var id = root.GetProperty("downloadId").GetString()!;
        if (_activeEngines.TryRemove(id, out var engine))
        {
            engine.Cancel();
        }
        if (_activeMediaDownloads.TryRemove(id, out var cts))
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        BroadcastIpcEvent(new { action = "cancel_download", downloadId = id, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    private void HandleDeleteDownloadFiles(JsonElement root)
    {
        var id = root.TryGetProperty("downloadId", out var idProp) ? idProp.GetString() : null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (_activeEngines.TryRemove(id, out var engine))
            {
                try { engine.Cancel(); engine.Dispose(); } catch { }
            }
            if (_activeMediaDownloads.TryRemove(id, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
            BroadcastIpcEvent(new { action = "cancel_download", downloadId = id, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }

        var filePath = root.TryGetProperty("filePath", out var fpProp) ? fpProp.GetString() : null;
        var fileName = root.TryGetProperty("fileName", out var fnProp) ? fnProp.GetString() : null;
        var destDir = root.TryGetProperty("destinationDirectory", out var ddProp) ? ddProp.GetString() : null;

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);

            try
            {
                var dir = !string.IsNullOrWhiteSpace(destDir) && Directory.Exists(destDir)
                    ? destDir
                    : (!string.IsNullOrWhiteSpace(filePath) && Directory.Exists(filePath)
                        ? filePath
                        : (!string.IsNullOrWhiteSpace(filePath) ? Path.GetDirectoryName(filePath) : null))
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                {
                    try { File.Delete(filePath); } catch { }
                    try { WDM.Core.Storage.CheckpointStore.DeleteCheckpoint(filePath); } catch { }
                }

                if (!string.IsNullOrWhiteSpace(fileName) && Directory.Exists(dir))
                {
                    var cleanName = HttpProbe.SanitizeFileName(fileName);
                    var exactTarget = Path.Combine(dir, cleanName);
                    if (File.Exists(exactTarget))
                    {
                        try { File.Delete(exactTarget); } catch { }
                        try { WDM.Core.Storage.CheckpointStore.DeleteCheckpoint(exactTarget); } catch { }
                    }

                    var baseName = Path.GetFileNameWithoutExtension(cleanName);
                    if (!string.IsNullOrWhiteSpace(baseName))
                    {
                        foreach (var f in Directory.EnumerateFiles(dir))
                        {
                            var name = Path.GetFileName(f);
                            if (name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
                                (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                                 name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                                 name.EndsWith(".wdm", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)))
                            {
                                try { File.Delete(f); } catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HandleDeleteDownloadFiles error]: {ex.Message}");
            }
        });
    }

    private void BroadcastIpcEvent(object payload)
    {
        try
        {
            var dir = Path.GetDirectoryName(_ipcEventsFilePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.AppendAllText(_ipcEventsFilePath, json + Environment.NewLine);
        }
        catch { }
    }

    private static void HandleOpenFile(JsonElement root)
    {
        var path = root.TryGetProperty("filePath", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HandleOpenFile error]: {ex}");
        }
    }

    private static void HandleOpenFolder(JsonElement root)
    {
        var path = root.TryGetProperty("filePath", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HandleOpenFolder error]: {ex}");
        }
    }

    private async Task HandleSniffMediaAsync(JsonElement root, string? requestId)
    {
        var url = root.GetProperty("url").GetString()!;
        try
        {
            var meta = await _mediaExtractor.ExtractMetadataAsync(url);

            if (meta == null)
            {
                PostToWeb(new { requestId, action = "sniff_media_result", success = false, message = "Could not extract stream formats. Please verify the URL." });
                return;
            }

            PostToWeb(new
            {
                requestId,
                action = "sniff_media_result",
                success = true,
                data = new
                {
                    title = meta.Title,
                    uploader = meta.Uploader,
                    thumbnail = meta.ThumbnailUrl,
                    duration = meta.Duration.HasValue ? $"{meta.Duration.Value:hh\\:mm\\:ss}" : null,
                    formats = meta.Formats.Select(f => new
                    {
                        formatId = f.FormatId,
                        resolution = f.Resolution,
                        extension = f.Extension,
                        fileSizeBytes = f.FileSizeBytes,
                        note = f.Note,
                        isAudioOnly = f.IsAudioOnly
                    })
                }
            });
        }
        catch (Exception ex)
        {
            PostToWeb(new { requestId, action = "sniff_media_result", success = false, message = ex.Message });
        }
    }

    private async Task HandleDownloadMediaAsync(JsonElement root, string? requestId)
    {
        var url = root.GetProperty("url").GetString()!;
        var formatId = root.TryGetProperty("formatId", out var fProp) ? fProp.GetString() : null;
        var title = root.TryGetProperty("title", out var tProp) ? tProp.GetString() : "Media Video";
        var fileName = root.TryGetProperty("fileName", out var fnProp) ? fnProp.GetString() : null;
        var destinationDirectory = root.TryGetProperty("destinationDirectory", out var dProp) ? dProp.GetString() : null;
        var isAudioOnly = root.TryGetProperty("isAudioOnly", out var aoProp) && aoProp.GetBoolean();
        var downloadId = (root.TryGetProperty("downloadId", out var idProp) ? idProp.GetString() : null) ?? Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();
        _activeMediaDownloads[downloadId] = cts;

        PostToWeb(new
        {
            requestId,
            action = "media_download_started",
            downloadId,
            url,
            title,
            fileName = fileName ?? $"{title}.mp4",
            formatId,
            destinationDirectory
        });

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
                    progressCallback: (percent, statusLine) =>
                    {
                        PostToWeb(new
                        {
                            action = "media_download_progress",
                            downloadId,
                            percentage = percent,
                            status = statusLine
                        });
                    },
                    cancellationToken: cts.Token);

                PostToWeb(new
                {
                    action = "media_download_completed",
                    downloadId,
                    filePath = completedPath,
                    destinationDirectory = Directory.Exists(completedPath) ? completedPath : (Path.GetDirectoryName(completedPath) ?? destinationDirectory)
                });
            }
            catch (OperationCanceledException)
            {
                PostToWeb(new
                {
                    action = "media_download_failed",
                    downloadId,
                    error = "Download stopped by user"
                });
            }
            catch (Exception ex)
            {
                PostToWeb(new
                {
                    action = "media_download_failed",
                    downloadId,
                    error = ex.Message
                });
            }
            finally
            {
                _activeMediaDownloads.TryRemove(downloadId, out _);
                cts.Dispose();
            }
        });

        await Task.CompletedTask;
    }

    private void HandleBrowseFolder(string? requestId)
    {
        Dispatcher.Invoke(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Destination Folder"
            };
            var result = dialog.ShowDialog(this);
            if (result == true)
            {
                PostToWeb(new { requestId, action = "browse_folder_result", success = true, folderPath = dialog.FolderName });
            }
            else
            {
                PostToWeb(new { requestId, action = "browse_folder_result", success = false });
            }
        });
    }

    private void HandleGetDefaultFolder(string? requestId)
    {
        var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        PostToWeb(new { requestId, action = "default_folder_result", folderPath = defaultDir });
    }

    private void HandleGetClipboard(string? requestId)
    {
        Dispatcher.Invoke(() =>
        {
            string? text = null;
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    var data = Clipboard.GetDataObject();
                    if (data != null)
                    {
                        if (data.GetDataPresent(DataFormats.UnicodeText))
                        {
                            text = data.GetData(DataFormats.UnicodeText) as string;
                        }
                        else if (data.GetDataPresent(DataFormats.Text))
                        {
                            text = data.GetData(DataFormats.Text) as string;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        break;
                    }
                }
                catch
                {
                    Thread.Sleep(40);
                }
            }
            PostToWeb(new { requestId, action = "clipboard_result", text });
        });
    }

    private async Task HandleLoadHistoryAsync(string? requestId)
    {
        if (File.Exists(_historyFilePath))
        {
            var json = await File.ReadAllTextAsync(_historyFilePath);
            PostToWeb(new { requestId, action = "load_history_result", history = json });
        }
        else
        {
            PostToWeb(new { requestId, action = "load_history_result", history = "[]" });
        }
    }

    private async Task HandleSaveHistoryAsync(JsonElement root)
    {
        if (root.TryGetProperty("history", out var histProp))
        {
            await File.WriteAllTextAsync(_historyFilePath, histProp.GetString() ?? "[]");
        }
    }

    private void PostToWeb(object message)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var json = JsonSerializer.Serialize(message, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                WebView?.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch
            {
                // CoreWebView2 might be closing
            }
        });
    }
}
