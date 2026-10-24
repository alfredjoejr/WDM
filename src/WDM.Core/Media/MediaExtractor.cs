using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WDM.Core.Media;

/// <summary>
/// Bridge for media stream catching (YouTube, HLS, DASH, TikTok, Twitter, Vimeo)
/// using yt-dlp and ffmpeg integration.
/// </summary>
public sealed partial class MediaExtractor : IMediaExtractor
{
    private static readonly string[] SupportedDomains =
    [
        "youtube.com", "youtu.be", "vimeo.com", "tiktok.com",
        "twitter.com", "x.com", "instagram.com", "twitch.tv",
        "soundcloud.com", "reddit.com", "dailymotion.com"
    ];

    public bool IsSupported(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || 
            url.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return SupportedDomains.Any(domain => uri.Host.EndsWith(domain, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    public async Task<bool> IsEngineAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            await proc.WaitForExitAsync(cancellationToken);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<MediaMetadataResult?> ExtractMetadataAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--dump-json --no-warnings --no-playlist \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            var json = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Unknown Media" : "Unknown Media";
            var uploader = root.TryGetProperty("uploader", out var uploaderProp) ? uploaderProp.GetString() : null;
            var thumbnail = root.TryGetProperty("thumbnail", out var thumbProp) ? thumbProp.GetString() : null;
            
            TimeSpan? duration = null;
            if (root.TryGetProperty("duration", out var durProp) && durProp.TryGetDouble(out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            var formatsList = new List<MediaFormatOption>();
            if (root.TryGetProperty("formats", out var formatsArray) && formatsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in formatsArray.EnumerateArray())
                {
                    var formatId = f.TryGetProperty("format_id", out var fid) ? fid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(formatId)) continue;

                    var resolution = f.TryGetProperty("resolution", out var res) ? res.GetString() ?? "audio" : "audio";
                    var ext = f.TryGetProperty("ext", out var extProp) ? extProp.GetString() : "mp4";
                    var note = f.TryGetProperty("format_note", out var noteProp) ? noteProp.GetString() : null;
                    var vcodec = f.TryGetProperty("vcodec", out var vc) ? vc.GetString() : null;
                    bool isAudio = vcodec == "none" || resolution.Contains("audio", StringComparison.OrdinalIgnoreCase);

                    long? fileSize = null;
                    if (f.TryGetProperty("filesize", out var fs) && fs.TryGetInt64(out var sizeVal))
                    {
                        fileSize = sizeVal;
                    }

                    formatsList.Add(new MediaFormatOption
                    {
                        FormatId = formatId,
                        Resolution = isAudio ? "Audio Only" : resolution,
                        Extension = ext,
                        FileSizeBytes = fileSize,
                        Note = note,
                        IsAudioOnly = isAudio
                    });
                }
            }

            return new MediaMetadataResult
            {
                Url = url,
                Title = title,
                Uploader = uploader,
                Duration = duration,
                ThumbnailUrl = thumbnail,
                Formats = formatsList
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> DownloadMediaAsync(
        string url, 
        string? formatId = null, 
        string? destinationDirectory = null, 
        Action<double, string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var outDir = destinationDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var outputTemplate = Path.Combine(outDir, "%(title)s.%(ext)s");

        var formatArg = !string.IsNullOrWhiteSpace(formatId)
            ? $"-f \"{formatId}+bestaudio/best\""
            : "-f \"bestvideo+bestaudio/best\"";

        var arguments = $"{formatArg} --newline --progress -o \"{outputTemplate}\" \"{url}\"";

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            // Sample yt-dlp output: [download]  45.2% of 120.45MiB at 12.34MiB/s ETA 00:05
            var match = ProgressRegex().Match(e.Data);
            if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
            {
                progressCallback?.Invoke(percent, e.Data);
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();

        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Media download failed: {err}");
        }

        return outDir;
    }

    [GeneratedRegex(@"\[download\]\s+(\d+(?:\.\d+)?)%")]
    private static partial Regex ProgressRegex();
}
