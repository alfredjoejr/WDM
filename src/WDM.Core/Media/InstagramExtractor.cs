using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WDM.Core.Engine;
using WDM.Core.Models;
using WDM.Core.Network;

namespace WDM.Core.Media;

/// <summary>
/// First-class Instagram extractor supporting Story Highlights, User Stories,
/// Reels, Video Posts, Photo Posts, and Multi-Slide Carousels using authenticated
/// SSR Relay payloads, Instagram Web API fallback, and WAL-aware browser cookie sync.
/// </summary>
public sealed partial class InstagramExtractor
{
    private const string IgAppId = "936619743392459";
    private const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0";
    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static string? _cachedCookieHeader;
    private static DateTime _cachedCookieTimeUtc = DateTime.MinValue;
    private static readonly object CookieLock = new();

    public sealed class IgSlideItem
    {
        public string Pk { get; init; } = string.Empty;
        public bool IsVideo { get; init; }
        public string? BestVideoUrl { get; init; }
        public string? SdVideoUrl { get; init; }
        public string? BestImageUrl { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double? DurationSeconds { get; init; }
    }

    public sealed class IgMediaCollection
    {
        public required string Title { get; init; }
        public string? Username { get; init; }
        public string? ThumbnailUrl { get; init; }
        public int? ActiveSlideIndex { get; init; }
        public List<IgSlideItem> Items { get; init; } = [];
    }

    public static bool IsInstagramUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Host.EndsWith("instagram.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("instagr.am", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Persists live Instagram cookies forwarded by the browser extension so both
    /// NativeHost and WDM.UI can authenticate regardless of browser cookie DB locks.
    /// </summary>
    public static void SaveBrowserCookies(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader) || !cookieHeader.Contains("sessionid=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (CookieLock)
        {
            _cachedCookieHeader = cookieHeader.Trim();
            _cachedCookieTimeUtc = DateTime.UtcNow;
            try
            {
                var wdmDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
                Directory.CreateDirectory(wdmDir);
                File.WriteAllText(Path.Combine(wdmDir, "ig_cookies.dat"), _cachedCookieHeader, Encoding.UTF8);
            }
            catch { }
        }
    }

    public static string? ResolveInstagramCookieHeader(out string? netscapeCookieFilePath)
    {
        netscapeCookieFilePath = null;
        var wdmDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
        Directory.CreateDirectory(wdmDir);
        var netscapePath = Path.Combine(wdmDir, "ig_cookies_netscape.txt");

        // 1. Always try live Firefox / Zen SQLite + WAL first (instant, reflects latest browser session)
        var sqliteCookies = TryReadFirefoxWalCookies(netscapePath);
        if (!string.IsNullOrWhiteSpace(sqliteCookies) && sqliteCookies.Contains("sessionid=", StringComparison.OrdinalIgnoreCase))
        {
            netscapeCookieFilePath = File.Exists(netscapePath) ? netscapePath : null;
            lock (CookieLock)
            {
                _cachedCookieHeader = sqliteCookies;
                _cachedCookieTimeUtc = DateTime.UtcNow;
            }
            return sqliteCookies;
        }

        // 2. Check in-memory / persisted extension cookies (e.g. from Chrome, Edge, Brave via chrome.cookies API)
        lock (CookieLock)
        {
            if (!string.IsNullOrWhiteSpace(_cachedCookieHeader) && (DateTime.UtcNow - _cachedCookieTimeUtc).TotalHours < 12)
            {
                WriteNetscapeCookiesFromHeader(_cachedCookieHeader, netscapePath);
                netscapeCookieFilePath = File.Exists(netscapePath) ? netscapePath : null;
                return _cachedCookieHeader;
            }

            var savedPath = Path.Combine(wdmDir, "ig_cookies.dat");
            if (File.Exists(savedPath))
            {
                try
                {
                    var saved = File.ReadAllText(savedPath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(saved))
                    {
                        _cachedCookieHeader = saved;
                        _cachedCookieTimeUtc = DateTime.UtcNow;
                        WriteNetscapeCookiesFromHeader(saved, netscapePath);
                        netscapeCookieFilePath = File.Exists(netscapePath) ? netscapePath : null;
                        return saved;
                    }
                }
                catch { }
            }
        }

        return sqliteCookies;
    }

    private static void WriteNetscapeCookiesFromHeader(string cookieHeader, string targetPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            var exp = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part[..eq].Trim();
                var val = part[(eq + 1)..].Trim();
                sb.AppendLine($".instagram.com\tTRUE\t/\tTRUE\t{exp}\t{name}\t{val}");
            }
            File.WriteAllText(targetPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static string? TryReadFirefoxWalCookies(string netscapeOutPath)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var profileRoots = new[]
            {
                Path.Combine(appData, "Mozilla", "Firefox", "Profiles"),
                Path.Combine(appData, "zen", "Profiles"),
                Path.Combine(appData, "Floorp", "Profiles"),
                Path.Combine(appData, "Waterfox", "Profiles")
            };

            var cookieFiles = new List<FileInfo>();
            foreach (var root in profileRoots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var db = Path.Combine(dir, "cookies.sqlite");
                    if (File.Exists(db))
                    {
                        var wal = db + "-wal";
                        var info = File.Exists(wal) ? new FileInfo(wal) : new FileInfo(db);
                        cookieFiles.Add(new FileInfo(db));
                    }
                }
            }

            // Order profiles by most recently updated WAL or DB
            var ordered = cookieFiles
                .OrderByDescending(f =>
                {
                    var wal = f.FullName + "-wal";
                    return File.Exists(wal) ? File.GetLastWriteTimeUtc(wal) : f.LastWriteTimeUtc;
                })
                .ToList();

            foreach (var dbFile in ordered)
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"wdm_ig_ck_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                try
                {
                    var tempDb = Path.Combine(tempDir, "cookies.sqlite");
                    CopySharedFile(dbFile.FullName, tempDb);
                    if (File.Exists(dbFile.FullName + "-wal"))
                    {
                        CopySharedFile(dbFile.FullName + "-wal", tempDb + "-wal");
                    }
                    if (File.Exists(dbFile.FullName + "-shm"))
                    {
                        CopySharedFile(dbFile.FullName + "-shm", tempDb + "-shm");
                    }

                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var netscapeLines = new StringBuilder();
                    netscapeLines.AppendLine("# Netscape HTTP Cookie File");

                    using (var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly"))
                    {
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT host, isHttpOnly, path, isSecure, expiry, name, value FROM moz_cookies WHERE host LIKE '%instagram.com%'";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var host = reader.IsDBNull(0) ? ".instagram.com" : reader.GetString(0);
                            var path = reader.IsDBNull(2) ? "/" : reader.GetString(2);
                            var isSec = !reader.IsDBNull(3) && reader.GetInt64(3) != 0;
                            var expiry = reader.IsDBNull(4) ? DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds() : reader.GetInt64(4);
                            var name = reader.IsDBNull(5) ? "" : reader.GetString(5);
                            var val = reader.IsDBNull(6) ? "" : reader.GetString(6);
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            dict[name] = val;
                            var includeSub = host.StartsWith('.') ? "TRUE" : "FALSE";
                            var secStr = isSec ? "TRUE" : "FALSE";
                            netscapeLines.AppendLine($"{host}\t{includeSub}\t{path}\t{secStr}\t{expiry}\t{name}\t{val}");
                        }
                    }

                    SqliteConnection.ClearAllPools();

                    if (dict.Count > 0)
                    {
                        var header = string.Join("; ", dict.Select(kv => $"{kv.Key}={kv.Value}"));
                        if (dict.ContainsKey("sessionid"))
                        {
                            try { File.WriteAllText(netscapeOutPath, netscapeLines.ToString(), Encoding.UTF8); } catch { }
                            return header;
                        }
                    }
                }
                catch { }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
        catch { }

        return null;
    }

    private static void CopySharedFile(string source, string dest)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    public async Task<IgMediaCollection?> ExtractCollectionAsync(string rawUrl, CancellationToken ct = default)
    {
        if (!IsInstagramUrl(rawUrl)) return null;

        // Parse optional slide index hint (?slide=5, ?img_index=2, #wdm-slide=5)
        int? slideHintOneBased = ParseSlideHint(rawUrl);
        var cleanUrl = StripWdmHash(rawUrl);

        var highlightMatch = HighlightRegex().Match(cleanUrl);
        var storyMatch = !highlightMatch.Success ? StoryRegex().Match(cleanUrl) : Match.Empty;
        var postMatch = PostOrReelRegex().Match(cleanUrl);

        string? targetHighlightId = highlightMatch.Success ? highlightMatch.Groups["id"].Value : null;
        string? targetStoryUser = storyMatch.Success ? storyMatch.Groups["user"].Value : null;
        string? targetStoryPk = storyMatch.Success && storyMatch.Groups["pk"].Success ? storyMatch.Groups["pk"].Value : null;
        string? targetShortcode = postMatch.Success ? postMatch.Groups["code"].Value : null;

        var cookieHeader = ResolveInstagramCookieHeader(out _);
        var csrfToken = ExtractCookieValue(cookieHeader, "csrftoken");

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };

        // Step 1: Fetch SSR HTML page with navigation headers
        string? html = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, cleanUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                html = await resp.Content.ReadAsStringAsync(ct);
            }
        }
        catch { }

        var reels = new List<JsonElement>();
        var mediaNodes = new List<JsonElement>();

        if (!string.IsNullOrWhiteSpace(html))
        {
            foreach (Match m in ScriptJsonRegex().Matches(html))
            {
                var jsonText = m.Groups[1].Value;
                if (!jsonText.Contains("image_versions2", StringComparison.Ordinal) &&
                    !jsonText.Contains("video_versions", StringComparison.Ordinal) &&
                    !jsonText.Contains("carousel_media", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(jsonText);
                    WalkInstagramJson(doc.RootElement.Clone(), reels, mediaNodes);
                }
                catch { }
            }
        }

        // Step 2: If Post/Reel wasn't found in SSR HTML, query /api/v1/media/{mediaId}/info/
        if (reels.Count == 0 && mediaNodes.Count == 0 && !string.IsNullOrWhiteSpace(targetShortcode) && !string.IsNullOrWhiteSpace(cookieHeader))
        {
            var mediaId = ShortcodeToMediaId(targetShortcode);
            if (!string.IsNullOrWhiteSpace(mediaId))
            {
                try
                {
                    using var apiReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.instagram.com/api/v1/media/{mediaId}/info/");
                    apiReq.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
                    apiReq.Headers.TryAddWithoutValidation("X-IG-App-ID", IgAppId);
                    apiReq.Headers.TryAddWithoutValidation("X-ASBD-ID", "129477");
                    apiReq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    apiReq.Headers.TryAddWithoutValidation("Referer", cleanUrl);
                    apiReq.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                    if (!string.IsNullOrWhiteSpace(csrfToken))
                    {
                        apiReq.Headers.TryAddWithoutValidation("X-CSRFToken", csrfToken);
                    }

                    using var apiResp = await client.SendAsync(apiReq, ct);
                    if (apiResp.IsSuccessStatusCode)
                    {
                        var apiJson = await apiResp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(apiJson);
                        WalkInstagramJson(doc.RootElement.Clone(), reels, mediaNodes);
                    }
                }
                catch { }
            }
        }

        // Case A: Story Highlight or User Story Reel
        if (reels.Count > 0)
        {
            JsonElement chosenReel = reels[0];
            foreach (var r in reels)
            {
                var rId = GetString(r, "id") ?? "";
                var rUser = r.TryGetProperty("user", out var uObj) && uObj.ValueKind == JsonValueKind.Object ? GetString(uObj, "username") : null;
                if (!string.IsNullOrWhiteSpace(targetHighlightId) && rId.Contains(targetHighlightId, StringComparison.OrdinalIgnoreCase))
                {
                    chosenReel = r;
                    break;
                }
                if (!string.IsNullOrWhiteSpace(targetStoryUser) && string.Equals(rUser, targetStoryUser, StringComparison.OrdinalIgnoreCase))
                {
                    chosenReel = r;
                    break;
                }
            }

            var reelTitle = GetString(chosenReel, "title");
            var username = chosenReel.TryGetProperty("user", out var usr) && usr.ValueKind == JsonValueKind.Object
                ? GetString(usr, "username")
                : targetStoryUser;

            var displayTitle = !string.IsNullOrWhiteSpace(reelTitle)
                ? (!string.IsNullOrWhiteSpace(username) ? $"{username} - {reelTitle}" : reelTitle)
                : (!string.IsNullOrWhiteSpace(username) ? $"{username} Story" : "Instagram Story");

            var items = new List<IgSlideItem>();
            int? activeIdx = null;

            if (chosenReel.TryGetProperty("items", out var itemsArr) && itemsArr.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var itemEl in itemsArr.EnumerateArray())
                {
                    var parsed = ParseSingleMediaNode(itemEl);
                    if (parsed != null)
                    {
                        if (!string.IsNullOrWhiteSpace(targetStoryPk) && parsed.Pk == targetStoryPk)
                        {
                            activeIdx = items.Count;
                        }
                        items.Add(parsed);
                    }
                    idx++;
                }
            }

            if (activeIdx == null && slideHintOneBased.HasValue && slideHintOneBased.Value >= 1 && slideHintOneBased.Value <= items.Count)
            {
                activeIdx = slideHintOneBased.Value - 1;
            }

            if (items.Count > 0)
            {
                var thumb = items[activeIdx ?? 0].BestImageUrl;
                return new IgMediaCollection
                {
                    Title = displayTitle,
                    Username = username,
                    ThumbnailUrl = thumb,
                    ActiveSlideIndex = activeIdx,
                    Items = items
                };
            }
        }

        // Case B: Post, Reel, or Multi-Slide Carousel
        if (mediaNodes.Count > 0)
        {
            JsonElement chosenNode = mediaNodes[0];
            if (!string.IsNullOrWhiteSpace(targetShortcode))
            {
                foreach (var n in mediaNodes)
                {
                    if (string.Equals(GetString(n, "code"), targetShortcode, StringComparison.Ordinal))
                    {
                        chosenNode = n;
                        break;
                    }
                }
            }

            var username = chosenNode.TryGetProperty("user", out var usr) && usr.ValueKind == JsonValueKind.Object
                ? GetString(usr, "username")
                : null;
            var code = GetString(chosenNode, "code") ?? targetShortcode ?? "post";
            string? caption = null;
            if (chosenNode.TryGetProperty("caption", out var capObj) && capObj.ValueKind == JsonValueKind.Object)
            {
                caption = GetString(capObj, "text");
            }
            if (!string.IsNullOrWhiteSpace(caption))
            {
                caption = caption.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (caption.Length > 48) caption = caption[..48].Trim();
            }

            var title = !string.IsNullOrWhiteSpace(caption)
                ? (!string.IsNullOrWhiteSpace(username) ? $"{username} - {caption}" : caption)
                : (!string.IsNullOrWhiteSpace(username) ? $"{username}_{code}" : $"Instagram_{code}");

            var items = new List<IgSlideItem>();
            if (chosenNode.TryGetProperty("carousel_media", out var carArr) && carArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var cEl in carArr.EnumerateArray())
                {
                    var parsed = ParseSingleMediaNode(cEl);
                    if (parsed != null) items.Add(parsed);
                }
            }
            else
            {
                var parsed = ParseSingleMediaNode(chosenNode);
                if (parsed != null) items.Add(parsed);
            }

            int? activeIdx = null;
            if (slideHintOneBased.HasValue && slideHintOneBased.Value >= 1 && slideHintOneBased.Value <= items.Count)
            {
                activeIdx = slideHintOneBased.Value - 1;
            }

            if (items.Count > 0)
            {
                return new IgMediaCollection
                {
                    Title = title,
                    Username = username,
                    ThumbnailUrl = items[activeIdx ?? 0].BestImageUrl,
                    ActiveSlideIndex = activeIdx,
                    Items = items
                };
            }
        }

        return null;
    }

    public async Task<MediaMetadataResult?> ExtractMetadataAsync(string url, CancellationToken ct = default)
    {
        var collection = await ExtractCollectionAsync(url, ct);
        if (collection == null || collection.Items.Count == 0)
        {
            return null;
        }

        var formats = new List<MediaFormatOption>();

        if (collection.Items.Count == 1)
        {
            var item = collection.Items[0];
            if (item.IsVideo && !string.IsNullOrWhiteSpace(item.BestVideoUrl))
            {
                var resLabel = item.Height > 0 ? $"{Math.Min(item.Width, item.Height)}p HD" : "HD Video";
                formats.Add(new MediaFormatOption
                {
                    FormatId = "ig_slide_1",
                    Resolution = resLabel,
                    Extension = "mp4",
                    Note = item.Width > 0 && item.Height > 0 ? $"{item.Width}x{item.Height}" : "Video + Audio",
                    DirectUrl = item.BestVideoUrl
                });

                if (!string.IsNullOrWhiteSpace(item.SdVideoUrl) && item.SdVideoUrl != item.BestVideoUrl)
                {
                    formats.Add(new MediaFormatOption
                    {
                        FormatId = "ig_slide_1_sd",
                        Resolution = "SD Video",
                        Extension = "mp4",
                        Note = "Standard",
                        DirectUrl = item.SdVideoUrl
                    });
                }

                formats.Add(new MediaFormatOption
                {
                    FormatId = "ig_slide_1_audio",
                    Resolution = "Audio Track",
                    Extension = "mp3",
                    Note = "MP3 Audio",
                    IsAudioOnly = true,
                    DirectUrl = item.BestVideoUrl
                });

                if (!string.IsNullOrWhiteSpace(item.BestImageUrl))
                {
                    formats.Add(new MediaFormatOption
                    {
                        FormatId = "ig_slide_1_cover",
                        Resolution = "Cover Photo",
                        Extension = "jpg",
                        Note = "Thumbnail HD",
                        IsImage = true,
                        DirectUrl = item.BestImageUrl
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(item.BestImageUrl))
            {
                formats.Add(new MediaFormatOption
                {
                    FormatId = "ig_slide_1",
                    Resolution = item.Width > 0 && item.Height > 0 ? $"HD Photo ({item.Width}x{item.Height})" : "HD Photo",
                    Extension = "jpg",
                    Note = "Original Image",
                    IsImage = true,
                    DirectUrl = item.BestImageUrl
                });
            }
        }
        else
        {
            // Multi-slide Highlight / Story / Carousel Post
            if (collection.ActiveSlideIndex.HasValue)
            {
                int curIdx = collection.ActiveSlideIndex.Value;
                var cur = collection.Items[curIdx];
                formats.Add(new MediaFormatOption
                {
                    FormatId = $"ig_slide_{curIdx + 1}",
                    Resolution = $"★ Current (#{curIdx + 1} of {collection.Items.Count})",
                    Extension = cur.IsVideo ? "mp4" : "jpg",
                    Note = cur.IsVideo ? "Current Video" : "Current Photo",
                    IsImage = !cur.IsVideo,
                    DirectUrl = cur.IsVideo ? cur.BestVideoUrl : cur.BestImageUrl
                });
            }

            formats.Add(new MediaFormatOption
            {
                FormatId = "ig_all_slides",
                Resolution = $"All {collection.Items.Count} Slides",
                Extension = "mp4",
                Note = $"Batch ({collection.Items.Count} items)"
            });

            for (int i = 0; i < collection.Items.Count; i++)
            {
                if (collection.ActiveSlideIndex == i) continue; // Already listed at top as Current
                var s = collection.Items[i];
                var dim = s.Width > 0 && s.Height > 0 ? $"{s.Width}x{s.Height}" : (s.IsVideo ? "HD Video" : "HD Photo");
                formats.Add(new MediaFormatOption
                {
                    FormatId = $"ig_slide_{i + 1}",
                    Resolution = $"Slide #{i + 1} • {(s.IsVideo ? "Video" : "Photo")}",
                    Extension = s.IsVideo ? "mp4" : "jpg",
                    Note = dim,
                    IsImage = !s.IsVideo,
                    DirectUrl = s.IsVideo ? s.BestVideoUrl : s.BestImageUrl
                });
            }
        }

        double? totalDur = collection.Items.Count == 1 ? collection.Items[0].DurationSeconds : null;

        return new MediaMetadataResult
        {
            Url = url,
            Title = collection.Title,
            Uploader = collection.Username,
            Duration = totalDur.HasValue ? TimeSpan.FromSeconds(totalDur.Value) : null,
            ThumbnailUrl = collection.ThumbnailUrl,
            Formats = formats
        };
    }

    public async Task<string?> TryDownloadAsync(
        string url,
        string? formatId,
        string? directUrl,
        string? destinationDirectory,
        string? outputFileName,
        bool isAudioOnly,
        Action<double, string>? progressCallback,
        CancellationToken ct)
    {
        var outDir = !string.IsNullOrWhiteSpace(destinationDirectory)
            ? destinationDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(outDir);

        // Fast path: if direct CDN URL is already known (and not batch ig_all_slides), download directly via 16-stream DownloadEngine!
        if (!string.IsNullOrWhiteSpace(directUrl) && formatId != "ig_all_slides")
        {
            return await DownloadSingleDirectStreamAsync(directUrl, outDir, outputFileName ?? "Instagram_Media.mp4", isAudioOnly, progressCallback, ct);
        }

        if (!IsInstagramUrl(url)) return null;

        var collection = await ExtractCollectionAsync(url, ct);
        if (collection == null || collection.Items.Count == 0)
        {
            return null;
        }

        // Batch download all slides in Highlight / Story / Carousel
        if (string.Equals(formatId, "ig_all_slides", StringComparison.OrdinalIgnoreCase))
        {
            var folderName = HttpProbe.SanitizeFileName(collection.Title);
            if (string.IsNullOrWhiteSpace(folderName)) folderName = "Instagram_Collection";
            var batchDir = Path.Combine(outDir, folderName);
            Directory.CreateDirectory(batchDir);

            string? lastSaved = null;
            for (int i = 0; i < collection.Items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var slide = collection.Items[i];
                var targetUrl = slide.IsVideo ? slide.BestVideoUrl : slide.BestImageUrl;
                if (string.IsNullOrWhiteSpace(targetUrl)) continue;

                var ext = slide.IsVideo ? (isAudioOnly ? "mp3" : "mp4") : "jpg";
                var itemName = $"{(i + 1):D2}_{folderName}.{ext}";
                int slideIdx = i;
                int totalCount = collection.Items.Count;

                lastSaved = await DownloadSingleDirectStreamAsync(
                    targetUrl,
                    batchDir,
                    itemName,
                    isAudioOnly && slide.IsVideo,
                    (pct, line) =>
                    {
                        var safePct = pct < 0 ? 0 : pct;
                        var overall = ((slideIdx * 100.0) + safePct) / totalCount;
                        progressCallback?.Invoke(Math.Round(overall, 1), $"[download] {overall:F1}% (Slide {slideIdx + 1}/{totalCount}) {line}");
                    },
                    ct);
            }

            return collection.Items.Count == 1 && lastSaved != null ? lastSaved : batchDir;
        }

        // Resolve single slide target
        int chosenIndex = collection.ActiveSlideIndex ?? 0;
        bool forceCover = false;
        bool forceSd = false;

        if (!string.IsNullOrWhiteSpace(formatId))
        {
            forceCover = formatId.EndsWith("_cover", StringComparison.OrdinalIgnoreCase);
            forceSd = formatId.EndsWith("_sd", StringComparison.OrdinalIgnoreCase);
            if (formatId.EndsWith("_audio", StringComparison.OrdinalIgnoreCase))
            {
                isAudioOnly = true;
            }

            var m = SlideFormatRegex().Match(formatId);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var parsedNum) && parsedNum >= 1 && parsedNum <= collection.Items.Count)
            {
                chosenIndex = parsedNum - 1;
            }
        }

        var targetItem = collection.Items[chosenIndex];
        string? mediaDirectUrl;
        string defaultExt;

        if (forceCover || !targetItem.IsVideo)
        {
            mediaDirectUrl = targetItem.BestImageUrl;
            defaultExt = "jpg";
            isAudioOnly = false;
        }
        else
        {
            mediaDirectUrl = (forceSd && !string.IsNullOrWhiteSpace(targetItem.SdVideoUrl))
                ? targetItem.SdVideoUrl
                : targetItem.BestVideoUrl;
            defaultExt = isAudioOnly ? "mp3" : "mp4";
        }

        if (string.IsNullOrWhiteSpace(mediaDirectUrl))
        {
            return null;
        }

        var baseTitle = HttpProbe.SanitizeFileName(collection.Title);
        if (collection.Items.Count > 1)
        {
            baseTitle = $"{baseTitle}_Slide{chosenIndex + 1}";
        }

        string finalName;
        if (!string.IsNullOrWhiteSpace(outputFileName))
        {
            var sanitized = HttpProbe.SanitizeFileName(outputFileName);
            var desiredExt = $".{defaultExt}";
            if (!sanitized.EndsWith(desiredExt, StringComparison.OrdinalIgnoreCase))
            {
                sanitized = Path.ChangeExtension(sanitized, desiredExt);
            }
            finalName = sanitized;
        }
        else
        {
            finalName = $"{baseTitle}.{defaultExt}";
        }

        return await DownloadSingleDirectStreamAsync(mediaDirectUrl, outDir, finalName, isAudioOnly, progressCallback, ct);
    }

    private static async Task<string> DownloadSingleDirectStreamAsync(
        string cdnUrl,
        string outDir,
        string fileName,
        bool extractMp3Audio,
        Action<double, string>? progressCallback,
        CancellationToken ct)
    {
        var cleanFileName = HttpProbe.SanitizeFileName(fileName);
        var downloadTargetName = extractMp3Audio
            ? $"{Path.GetFileNameWithoutExtension(cleanFileName)}_temp_{Guid.NewGuid():N}.mp4"
            : cleanFileName;

        using var engine = new DownloadEngine();
        engine.ProgressChanged += snap =>
        {
            var pct = extractMp3Audio ? snap.ProgressPercentage * 0.85 : snap.ProgressPercentage;
            progressCallback?.Invoke(pct, $"[download] {pct:F1}% of ~{snap.TotalFormatted} at {snap.SpeedFormatted} ETA {(snap.EstimatedRemainingTime.HasValue ? $"{snap.EstimatedRemainingTime.Value:mm\\:ss}" : "00:00")}");
        };

        var options = new DownloadOptions
        {
            Url = cdnUrl,
            DestinationDirectory = outDir,
            CustomFileName = downloadTargetName,
            Connections = 8,
            CustomHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = DesktopUserAgent,
                ["Referer"] = "https://www.instagram.com/"
            }
        };

        var downloadedPath = await engine.StartAsync(options, ct);

        if (!extractMp3Audio)
        {
            progressCallback?.Invoke(100, "[download] 100% Completed");
            return downloadedPath;
        }

        // Convert video stream to high-quality MP3 using bundled FFmpeg
        progressCallback?.Invoke(90, "[ffmpeg] Extracting MP3 audio track...");
        var mp3FileName = Path.ChangeExtension(cleanFileName, ".mp3");
        var mp3Path = Path.Combine(outDir, mp3FileName);
        var ffmpegDir = MediaExtractor.GetFFmpegDirectory();
        var ffmpegExe = !string.IsNullOrWhiteSpace(ffmpegDir) ? Path.Combine(ffmpegDir, "ffmpeg.exe") : "ffmpeg";

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = $"-y -i \"{downloadedPath}\" -vn -acodec libmp3lame -q:a 2 \"{mp3Path}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            try { proc.StandardInput.Close(); } catch { }
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0 && File.Exists(mp3Path))
            {
                try { File.Delete(downloadedPath); } catch { }
                progressCallback?.Invoke(100, "[download] 100% Audio extracted");
                return mp3Path;
            }
        }
        catch { }

        return downloadedPath;
    }

    private static void WalkInstagramJson(JsonElement el, List<JsonElement> reels, List<JsonElement> mediaNodes)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                // 1. Check if this object is a Story / Highlight Reel (has "items" array of media items)
                if (el.TryGetProperty("items", out var itemsProp) &&
                    itemsProp.ValueKind == JsonValueKind.Array &&
                    itemsProp.GetArrayLength() > 0)
                {
                    var first = itemsProp[0];
                    if (first.ValueKind == JsonValueKind.Object &&
                        (first.TryGetProperty("image_versions2", out _) || first.TryGetProperty("video_versions", out _) || first.TryGetProperty("carousel_media", out _)))
                    {
                        // Distinguish between a Story Reel (has "id" or "reel_type" or items without carousel) vs a single media response {"items": [media]}
                        bool isStoryReel = el.TryGetProperty("reel_type", out _) ||
                                           (el.TryGetProperty("id", out var idP) && idP.ValueKind == JsonValueKind.String && (idP.GetString()?.StartsWith("highlight:", StringComparison.Ordinal) == true || el.TryGetProperty("seen", out _) || el.TryGetProperty("latest_reel_media", out _))) ||
                                           (itemsProp.GetArrayLength() > 1 && !first.TryGetProperty("carousel_media", out _));

                        if (isStoryReel)
                        {
                            reels.Add(el);
                            return;
                        }

                        foreach (var m in itemsProp.EnumerateArray())
                        {
                            if (m.ValueKind == JsonValueKind.Object)
                            {
                                mediaNodes.Add(m);
                            }
                        }
                        return;
                    }
                }

                // 2. Check if this object is an Instagram Post / Reel / Carousel media node
                if ((el.TryGetProperty("pk", out _) || el.TryGetProperty("code", out _)) &&
                    (el.TryGetProperty("carousel_media", out _) || el.TryGetProperty("video_versions", out _) || el.TryGetProperty("image_versions2", out _)))
                {
                    mediaNodes.Add(el);
                    return;
                }

                foreach (var prop in el.EnumerateObject())
                {
                    WalkInstagramJson(prop.Value, reels, mediaNodes);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    WalkInstagramJson(item, reels, mediaNodes);
                }
                break;
        }
    }

    private static IgSlideItem? ParseSingleMediaNode(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var pk = GetString(el, "pk") ?? GetString(el, "id") ?? Guid.NewGuid().ToString("N");
        int width = GetInt(el, "original_width");
        int height = GetInt(el, "original_height");
        double? duration = null;
        if (el.TryGetProperty("video_duration", out var vd) && vd.ValueKind == JsonValueKind.Number && vd.TryGetDouble(out var dVal))
        {
            duration = dVal;
        }

        string? bestVideo = null;
        string? sdVideo = null;

        if (el.TryGetProperty("video_versions", out var vv) && vv.ValueKind == JsonValueKind.Array && vv.GetArrayLength() > 0)
        {
            var list = new List<(int Type, int W, int H, string Url)>();
            foreach (var v in vv.EnumerateArray())
            {
                var u = GetString(v, "url");
                if (string.IsNullOrWhiteSpace(u)) continue;
                int vt = GetInt(v, "type");
                int vw = GetInt(v, "width");
                int vh = GetInt(v, "height");
                list.Add((vt, vw, vh, u));
            }

            if (list.Count > 0)
            {
                // Instagram type 101 is primary HD progressive MP4 (H.264 + AAC), or sort by pixel area descending
                var best = list.OrderByDescending(x => x.Type == 101 ? 1 : 0).ThenByDescending(x => x.W * x.H).First();
                bestVideo = best.Url;
                if (width == 0 && best.W > 0) width = best.W;
                if (height == 0 && best.H > 0) height = best.H;

                var last = list.Last();
                if (last.Url != bestVideo) sdVideo = last.Url;
            }
        }

        string? bestImg = null;
        if (el.TryGetProperty("image_versions2", out var iv2) && iv2.ValueKind == JsonValueKind.Object &&
            iv2.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
        {
            int bestArea = -1;
            foreach (var c in cands.EnumerateArray())
            {
                var u = GetString(c, "url");
                if (string.IsNullOrWhiteSpace(u)) continue;
                int cw = GetInt(c, "width");
                int ch = GetInt(c, "height");
                int area = cw * ch;
                if (bestImg == null || area > bestArea)
                {
                    bestArea = area;
                    bestImg = u;
                    if (width == 0 && cw > 0) width = cw;
                    if (height == 0 && ch > 0) height = ch;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(bestVideo) && string.IsNullOrWhiteSpace(bestImg))
        {
            return null;
        }

        return new IgSlideItem
        {
            Pk = pk,
            IsVideo = !string.IsNullOrWhiteSpace(bestVideo),
            BestVideoUrl = bestVideo,
            SdVideoUrl = sdVideo,
            BestImageUrl = bestImg,
            Width = width,
            Height = height,
            DurationSeconds = duration
        };
    }

    public static string? ShortcodeToMediaId(string shortcode)
    {
        if (string.IsNullOrWhiteSpace(shortcode)) return null;
        var clean = shortcode.Length > 11 ? shortcode[..11] : shortcode;
        BigInteger id = BigInteger.Zero;
        foreach (char c in clean)
        {
            int idx = Base64UrlAlphabet.IndexOf(c);
            if (idx < 0) return null;
            id = (id * 64) + idx;
        }
        return id > BigInteger.Zero ? id.ToString() : null;
    }

    private static int? ParseSlideHint(string url)
    {
        var m1 = SlideQueryRegex().Match(url);
        if (m1.Success && int.TryParse(m1.Groups[1].Value, out var n1) && n1 >= 1) return n1;
        var m2 = ImgIndexQueryRegex().Match(url);
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out var n2) && n2 >= 1) return n2;
        return null;
    }

    private static string StripWdmHash(string url)
    {
        var hashIdx = url.IndexOf('#');
        return hashIdx >= 0 ? url[..hashIdx] : url;
    }

    private static string? ExtractCookieValue(string? cookieHeader, string key)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return null;
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && string.Equals(part[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim();
            }
        }
        return null;
    }

    private static string? GetString(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null
        };
    }

    private static int GetInt(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v))
        {
            return v;
        }
        return 0;
    }

    [GeneratedRegex(@"instagram\.com/stories/highlights/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HighlightRegex();

    [GeneratedRegex(@"instagram\.com/stories/(?<user>[^/?#&]+)(?:/(?<pk>\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex StoryRegex();

    [GeneratedRegex(@"instagram\.com/(?:[^/?#&]+/)?(?:p|reel|reels|tv)/(?<code>[A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PostOrReelRegex();

    [GeneratedRegex(@"<script[^>]*type=[""']application/json[""'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptJsonRegex();

    [GeneratedRegex(@"(?:[?&#]wdm[_-]?slide=|[?&]slide=)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SlideQueryRegex();

    [GeneratedRegex(@"[?&]img_index=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ImgIndexQueryRegex();

    [GeneratedRegex(@"^ig_slide_(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SlideFormatRegex();
}
