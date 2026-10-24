using System.Collections.Concurrent;
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
public sealed class HostService
{
    private readonly HttpProbe _probe = new();
    private readonly MediaExtractor _mediaExtractor = new();
    private readonly ConcurrentDictionary<string, DownloadEngine> _activeDownloads = new();

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

        // Acknowledge start immediately to browser
        await HostProtocol.SendMessageAsync(new
        {
            requestId,
            action = "download_started",
            downloadId,
            url
        }, ct);

        // Run download in background and stream progress back
        _ = Task.Run(async () =>
        {
            try
            {
                engine.ProgressChanged += async snapshot =>
                {
                    await HostProtocol.SendMessageAsync(new
                    {
                        action = "download_progress",
                        downloadId,
                        percentage = snapshot.ProgressPercentage,
                        speed = snapshot.SpeedFormatted,
                        downloaded = snapshot.DownloadedFormatted,
                        total = snapshot.TotalFormatted,
                        activeConnections = snapshot.ActiveConnections
                    });
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

                await HostProtocol.SendMessageAsync(new
                {
                    action = "download_completed",
                    downloadId,
                    filePath = savedPath,
                    sha256 = hash
                });
            }
            catch (Exception ex)
            {
                await HostProtocol.SendMessageAsync(new
                {
                    action = "download_failed",
                    downloadId,
                    error = ex.Message
                });
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
        var metadata = await _mediaExtractor.ExtractMetadataAsync(url, ct);

        if (metadata == null)
        {
            await HostProtocol.SendMessageAsync(new
            {
                requestId,
                action = "sniff_media_result",
                success = false,
                message = "No streams could be extracted or yt-dlp is missing"
            }, ct);
            return;
        }

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

        var downloadId = Guid.NewGuid().ToString("N");

        await HostProtocol.SendMessageAsync(new
        {
            requestId,
            action = "media_download_started",
            downloadId
        }, ct);

        _ = Task.Run(async () =>
        {
            try
            {
                var targetDir = await _mediaExtractor.DownloadMediaAsync(
                    url,
                    formatId,
                    progressCallback: async (percent, line) =>
                    {
                        await HostProtocol.SendMessageAsync(new
                        {
                            action = "media_download_progress",
                            downloadId,
                            percentage = percent,
                            statusText = line
                        });
                    },
                    cancellationToken: ct);

                await HostProtocol.SendMessageAsync(new
                {
                    action = "media_download_completed",
                    downloadId,
                    destinationDirectory = targetDir
                });
            }
            catch (Exception ex)
            {
                await HostProtocol.SendMessageAsync(new
                {
                    action = "media_download_failed",
                    downloadId,
                    error = ex.Message
                });
            }
        }, ct);
    }
}
