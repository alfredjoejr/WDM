using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
    private readonly string _historyFilePath;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();
        
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
        Directory.CreateDirectory(appData);
        _historyFilePath = Path.Combine(appData, "downloads_history.json");

        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

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

        var downloadId = Guid.NewGuid().ToString("N");
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
    }

    private void HandleCancelDownload(JsonElement root)
    {
        var id = root.GetProperty("downloadId").GetString()!;
        if (_activeEngines.TryGetValue(id, out var engine))
        {
            engine.Cancel();
        }
    }

    private static void HandleOpenFile(JsonElement root)
    {
        var path = root.GetProperty("filePath").GetString();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private static void HandleOpenFolder(JsonElement root)
    {
        var path = root.GetProperty("filePath").GetString();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }

    private async Task HandleSniffMediaAsync(JsonElement root, string? requestId)
    {
        var url = root.GetProperty("url").GetString()!;
        var meta = await _mediaExtractor.ExtractMetadataAsync(url);

        if (meta == null)
        {
            PostToWeb(new { requestId, action = "sniff_media_result", success = false });
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

    private async Task HandleDownloadMediaAsync(JsonElement root, string? requestId)
    {
        var url = root.GetProperty("url").GetString()!;
        var formatId = root.TryGetProperty("formatId", out var fProp) ? fProp.GetString() : null;
        var downloadId = Guid.NewGuid().ToString("N");

        PostToWeb(new { requestId, action = "media_download_started", downloadId });

        _ = Task.Run(async () =>
        {
            try
            {
                var dir = await _mediaExtractor.DownloadMediaAsync(
                    url,
                    formatId,
                    progressCallback: (percent, statusLine) =>
                    {
                        PostToWeb(new
                        {
                            action = "media_download_progress",
                            downloadId,
                            percentage = percent,
                            status = statusLine
                        });
                    });

                PostToWeb(new
                {
                    action = "media_download_completed",
                    downloadId,
                    destinationDirectory = dir
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
        });

        await Task.CompletedTask;
    }

    private void HandleGetClipboard(string? requestId)
    {
        Dispatcher.Invoke(() =>
        {
            string? text = null;
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
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
