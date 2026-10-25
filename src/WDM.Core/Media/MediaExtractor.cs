using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WDM.Core.Network;

namespace WDM.Core.Media;

/// <summary>
/// Bridge for media stream catching (YouTube, HLS, DASH, TikTok, Twitter, Vimeo)
/// with automated zero-config engine bootstrapping and FFmpeg integration.
/// </summary>
public sealed partial class MediaExtractor : IMediaExtractor
{
    private static readonly string[] SupportedDomains =
    [
        "youtube.com", "youtu.be", "vimeo.com", "tiktok.com",
        "twitter.com", "x.com", "instagram.com", "twitch.tv",
        "soundcloud.com", "reddit.com", "dailymotion.com"
    ];

    private static string? _cachedYtDlpPath;
    private static string? _cachedFfmpegDir;

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

    public async Task<string> GetYtDlpBinaryPathAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedYtDlpPath) && File.Exists(_cachedYtDlpPath))
        {
            return _cachedYtDlpPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var toolsDir = Path.Combine(localAppData, "WDM", "tools");
        Directory.CreateDirectory(toolsDir);

        // Candidate search paths
        var candidates = new List<string>
        {
            Path.Combine(toolsDir, "yt-dlp.exe"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "yt-dlp.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"),
            Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe")
        };

        // Check WinGet packages folder if exists
        var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPackages))
        {
            try
            {
                var pkgExe = Directory.EnumerateFiles(wingetPackages, "yt-dlp.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (pkgExe != null) candidates.Add(pkgExe);
            }
            catch { }
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                _cachedYtDlpPath = path;
                return path;
            }
        }

        // Check system PATH
        if (await TestBinaryAsync("yt-dlp", cancellationToken))
        {
            _cachedYtDlpPath = "yt-dlp";
            return "yt-dlp";
        }

        // Automatic Self-Bootstrap: Download official standalone yt-dlp.exe directly
        var targetExe = Path.Combine(toolsDir, "yt-dlp.exe");
        const string downloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

        using var client = HttpClientFactory.CreateClient();
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(targetExe, FileMode.Create, FileAccess.Write, FileShare.None);
        await sourceStream.CopyToAsync(fileStream, cancellationToken);

        _cachedYtDlpPath = targetExe;
        return targetExe;
    }

    public static string? GetFFmpegDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_cachedFfmpegDir) && Directory.Exists(_cachedFfmpegDir)) return _cachedFfmpegDir;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        
        var wdmTools = Path.Combine(localAppData, "WDM", "tools");
        if (File.Exists(Path.Combine(wdmTools, "ffmpeg.exe")))
        {
            _cachedFfmpegDir = wdmTools;
            return wdmTools;
        }

        var wingetLinks = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        if (File.Exists(Path.Combine(wingetLinks, "ffmpeg.exe")))
        {
            _cachedFfmpegDir = wingetLinks;
            return wingetLinks;
        }

        var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPackages))
        {
            try
            {
                var pkgFfmpeg = Directory.EnumerateFiles(wingetPackages, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (pkgFfmpeg != null)
                {
                    var dir = Path.GetDirectoryName(pkgFfmpeg);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        _cachedFfmpegDir = dir;
                        return dir;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public async Task<bool> IsEngineAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var binary = await GetYtDlpBinaryPathAsync(cancellationToken);
            return !string.IsNullOrWhiteSpace(binary);
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
            var ytDlpPath = await GetYtDlpBinaryPathAsync(cancellationToken);
            var ffmpegDir = GetFFmpegDirectory();
            var ffmpegArg = !string.IsNullOrWhiteSpace(ffmpegDir) ? $"--ffmpeg-location \"{ffmpegDir}\"" : string.Empty;

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    Arguments = $"--dump-json --no-warnings --no-playlist {ffmpegArg} \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            var stdOutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdOutTask, stdErrTask);
            await proc.WaitForExitAsync(cancellationToken);

            var json = await stdOutTask;

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                var err = await stdErrTask;
                System.Diagnostics.Debug.WriteLine($"[yt-dlp extract error] exit={proc.ExitCode}: {err}");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Unknown Media" : "Unknown Media";
            var uploader = root.TryGetProperty("uploader", out var uploaderProp) ? uploaderProp.GetString() : null;
            var thumbnail = root.TryGetProperty("thumbnail", out var thumbProp) ? thumbProp.GetString() : null;
            
            TimeSpan? duration = null;
            if (root.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number && durProp.TryGetDouble(out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            var rawFormats = new List<MediaFormatOption>();
            if (root.TryGetProperty("formats", out var formatsArray) && formatsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in formatsArray.EnumerateArray())
                {
                    var formatId = f.TryGetProperty("format_id", out var fid) && fid.ValueKind == JsonValueKind.String ? fid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(formatId)) continue;
                    if (formatId.StartsWith("sb", StringComparison.OrdinalIgnoreCase)) continue; // ignore storyboard thumbnails

                    var ext = f.TryGetProperty("ext", out var extProp) && extProp.ValueKind == JsonValueKind.String ? extProp.GetString() : "mp4";
                    if (string.Equals(ext, "mhtml", StringComparison.OrdinalIgnoreCase)) continue;

                    var vcodec = f.TryGetProperty("vcodec", out var vc) && vc.ValueKind == JsonValueKind.String ? vc.GetString() : null;
                    var acodec = f.TryGetProperty("acodec", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() : null;
                    var note = f.TryGetProperty("format_note", out var noteProp) && noteProp.ValueKind == JsonValueKind.String ? noteProp.GetString() : null;

                    int height = 0;
                    if (f.TryGetProperty("height", out var hProp) && hProp.ValueKind == JsonValueKind.Number && hProp.TryGetInt32(out var hVal))
                    {
                        height = hVal;
                    }

                    bool isAudio = (vcodec == "none" || string.IsNullOrWhiteSpace(vcodec)) && acodec != "none";

                    string resolution;
                    if (isAudio)
                    {
                        resolution = "Audio";
                    }
                    else if (height > 0)
                    {
                        resolution = $"{height}p";
                    }
                    else if (f.TryGetProperty("resolution", out var resProp) && resProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(resProp.GetString()))
                    {
                        resolution = resProp.GetString()!;
                    }
                    else
                    {
                        resolution = "Video";
                    }

                    long? fileSize = null;
                    if (f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number && fs.TryGetInt64(out var sizeVal))
                    {
                        fileSize = sizeVal;
                    }
                    else if (f.TryGetProperty("filesize_approx", out var fsa) && fsa.ValueKind == JsonValueKind.Number && fsa.TryGetInt64(out var sizeValApprox))
                    {
                        fileSize = sizeValApprox;
                    }

                    rawFormats.Add(new MediaFormatOption
                    {
                        FormatId = formatId,
                        Resolution = resolution,
                        Extension = ext ?? "mp4",
                        FileSizeBytes = fileSize,
                        Note = note,
                        IsAudioOnly = isAudio
                    });
                }
            }

            // Deduplicate and curate clean user-friendly format options
            var formatsList = new List<MediaFormatOption>();

            var videoFormats = rawFormats
                .Where(f => !f.IsAudioOnly)
                .GroupBy(f => f.Resolution)
                .Select(g => g.OrderByDescending(f => f.FileSizeBytes ?? 0).First())
                .OrderByDescending(f =>
                {
                    var match = Regex.Match(f.Resolution, @"\d+");
                    return match.Success && int.TryParse(match.Value, out var n) ? n : 0;
                })
                .ToList();

            var audioFormats = rawFormats
                .Where(f => f.IsAudioOnly)
                .GroupBy(f => f.Extension)
                .Select(g => g.OrderByDescending(f => f.FileSizeBytes ?? 0).First())
                .ToList();

            formatsList.AddRange(videoFormats);
            formatsList.AddRange(audioFormats);

            if (formatsList.Count == 0)
            {
                formatsList = rawFormats;
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaExtractor Exception]: {ex}");
            return null;
        }
    }

    public async Task<string> DownloadMediaAsync(
        string url, 
        string? formatId = null, 
        string? destinationDirectory = null, 
        string? outputFileName = null,
        bool isAudioOnly = false,
        Action<double, string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var ytDlpPath = await GetYtDlpBinaryPathAsync(cancellationToken);
        var ffmpegDir = GetFFmpegDirectory();
        var ffmpegArg = !string.IsNullOrWhiteSpace(ffmpegDir) ? $"--ffmpeg-location \"{ffmpegDir}\"" : string.Empty;

        var outDir = destinationDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(outDir);

        string outputTemplate;
        if (!string.IsNullOrWhiteSpace(outputFileName))
        {
            var cleanFileName = HttpProbe.SanitizeFileName(outputFileName);
            var ext = Path.GetExtension(cleanFileName);
            if (string.IsNullOrWhiteSpace(ext))
            {
                outputTemplate = Path.Combine(outDir, $"{cleanFileName}.%(ext)s");
            }
            else
            {
                outputTemplate = Path.Combine(outDir, cleanFileName);
            }
        }
        else
        {
            outputTemplate = Path.Combine(outDir, "%(title)s.%(ext)s");
        }

        string formatArg;
        if (isAudioOnly)
        {
            formatArg = !string.IsNullOrWhiteSpace(formatId)
                ? $"-f \"{formatId}\" -x --audio-format mp3"
                : "-f \"bestaudio\" -x --audio-format mp3";
        }
        else if (!string.IsNullOrWhiteSpace(formatId))
        {
            formatArg = $"-f \"{formatId}+bestaudio/{formatId}/best\" --merge-output-format mp4";
        }
        else
        {
            formatArg = "-f \"bestvideo+bestaudio/best\" --merge-output-format mp4";
        }

        var arguments = $"{formatArg} {ffmpegArg} --newline --progress -o \"{outputTemplate}\" \"{url}\"";

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        string? downloadedFilePath = null;

        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            // Capture output file path from yt-dlp logs
            if (e.Data.Contains("Destination:", StringComparison.OrdinalIgnoreCase))
            {
                var idx = e.Data.IndexOf("Destination:", StringComparison.OrdinalIgnoreCase) + "Destination:".Length;
                downloadedFilePath = e.Data[idx..].Trim().Trim('"');
            }
            else if (e.Data.Contains("Merging formats into", StringComparison.OrdinalIgnoreCase))
            {
                var idx = e.Data.IndexOf("Merging formats into", StringComparison.OrdinalIgnoreCase) + "Merging formats into".Length;
                downloadedFilePath = e.Data[idx..].Trim().Trim('"');
            }

            var match = ProgressRegex().Match(e.Data);
            if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
            {
                progressCallback?.Invoke(percent, e.Data);
            }
            else
            {
                progressCallback?.Invoke(-1, e.Data);
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();

        var stdErrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0)
        {
            var err = await stdErrTask;
            throw new InvalidOperationException($"Media download failed (code {proc.ExitCode}): {err}");
        }

        if (!string.IsNullOrWhiteSpace(downloadedFilePath))
        {
            if (!Path.IsPathRooted(downloadedFilePath))
            {
                downloadedFilePath = Path.Combine(outDir, downloadedFilePath);
            }

            if (File.Exists(downloadedFilePath))
            {
                return downloadedFilePath;
            }
        }

        if (!string.IsNullOrWhiteSpace(outputFileName))
        {
            var clean = HttpProbe.SanitizeFileName(outputFileName);
            var candidate = Path.Combine(outDir, clean);
            if (File.Exists(candidate)) return candidate;

            var candMp4 = Path.Combine(outDir, $"{clean}.mp4");
            if (File.Exists(candMp4)) return candMp4;

            var candMp3 = Path.Combine(outDir, $"{clean}.mp3");
            if (File.Exists(candMp3)) return candMp3;

            var candWithoutExt = Path.Combine(outDir, Path.GetFileNameWithoutExtension(clean) + ".mp4");
            if (File.Exists(candWithoutExt)) return candWithoutExt;

            var candWithoutExtMp3 = Path.Combine(outDir, Path.GetFileNameWithoutExtension(clean) + ".mp3");
            if (File.Exists(candWithoutExtMp3)) return candWithoutExtMp3;
        }

        try
        {
            var dirInfo = new DirectoryInfo(outDir);
            var newestFile = dirInfo.EnumerateFiles()
                .Where(f => f.LastWriteTimeUtc >= DateTime.UtcNow.AddMinutes(-10))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newestFile != null)
            {
                return newestFile.FullName;
            }
        }
        catch { }

        return outDir;
    }

    private static async Task<bool> TestBinaryAsync(string binary, CancellationToken ct)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binary,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"\[download\]\s+(\d+(?:\.\d+)?)%")]
    private static partial Regex ProgressRegex();
}
