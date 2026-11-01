using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using WDM.Core.Models;

namespace WDM.Core.Network;

/// <summary>
/// Pre-flight inspector to determine server capabilities, range support, file size, and target file name.
/// </summary>
public sealed class HttpProbe
{
    private readonly HttpClient _httpClient;

    public HttpProbe(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HttpClientFactory.CreateClient();
    }

    public async Task<DownloadMetadata> ProbeAsync(
        string url, 
        Dictionary<string, string>? customHeaders = null, 
        CancellationToken cancellationToken = default)
    {
        // 1. First attempt: Standard HEAD request
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            ApplyCustomHeaders(headRequest, customHeaders);

            using var headResponse = await _httpClient.SendAsync(
                headRequest, 
                HttpCompletionOption.ResponseHeadersRead, 
                cancellationToken);

            if (headResponse.IsSuccessStatusCode)
            {
                var meta = ExtractMetadata(url, headResponse);
                if (meta.TotalBytes.HasValue && meta.TotalBytes.Value > 0)
                {
                    return meta;
                }
            }
        }
        catch
        {
            // Fall through to 1-byte GET range probe
        }

        // 2. Second attempt: 1-byte Range GET (Range: bytes=0-0)
        // This confirms range support and discovers total length even if HEAD was blocked or stripped.
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);
        ApplyCustomHeaders(rangeRequest, customHeaders);

        using var rangeResponse = await _httpClient.SendAsync(
            rangeRequest, 
            HttpCompletionOption.ResponseHeadersRead, 
            cancellationToken);

        rangeResponse.EnsureSuccessStatusCode();
        return ExtractMetadata(url, rangeResponse);
    }

    private static DownloadMetadata ExtractMetadata(string originalUrl, HttpResponseMessage response)
    {
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
        long? totalBytes = null;
        bool acceptsRanges = false;

        var isRangeRequest = response.RequestMessage?.Headers.Range != null;

        // Check Content-Range header (e.g., "bytes 0-0/104857600")
        if (response.Content.Headers.ContentRange != null)
        {
            if (response.Content.Headers.ContentRange.Length.HasValue)
            {
                totalBytes = response.Content.Headers.ContentRange.Length.Value;
                acceptsRanges = true;
            }
        }

        // Check standard Content-Length (only when full 200 OK response or HEAD, not a 1-byte 206 PartialContent slice)
        if (!totalBytes.HasValue && response.StatusCode != HttpStatusCode.PartialContent && response.Content.Headers.ContentLength.HasValue)
        {
            totalBytes = response.Content.Headers.ContentLength.Value;
        }

        // Check Accept-Ranges header:
        // If we explicitly sent a Range header and the server replied 200 OK instead of 206 PartialContent,
        // the server ignored our Range header and does NOT support range requests on this endpoint.
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            acceptsRanges = true;
        }
        else if (!isRangeRequest && response.Headers.AcceptRanges.Contains("bytes"))
        {
            acceptsRanges = true;
        }

        // Extract server-provided checksums if present (e.g. Content-MD5, x-amz-meta-digest, digest headers)
        string? serverMd5 = null;
        string? serverSha256 = null;

        if (response.Content.Headers.ContentMD5 != null)
        {
            serverMd5 = Convert.ToHexString(response.Content.Headers.ContentMD5).ToLowerInvariant();
        }

        if (response.Headers.TryGetValues("ETag", out var etagValues))
        {
            var rawEtag = etagValues.FirstOrDefault()?.Trim('"', ' ', 'W', '/', '\\');
            if (!string.IsNullOrWhiteSpace(rawEtag) && rawEtag.Length == 32 && rawEtag.All(Uri.IsHexDigit))
            {
                serverMd5 ??= rawEtag.ToLowerInvariant();
            }
        }

        if (response.Headers.TryGetValues("Digest", out var digestValues))
        {
            foreach (var d in digestValues)
            {
                var parts = d.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                {
                    if (part.StartsWith("sha-256=", StringComparison.OrdinalIgnoreCase))
                    {
                        var b64 = part["sha-256=".Length..].Trim();
                        try
                        {
                            serverSha256 = Convert.ToHexString(Convert.FromBase64String(b64)).ToLowerInvariant();
                        }
                        catch { }
                    }
                    else if (part.StartsWith("md5=", StringComparison.OrdinalIgnoreCase))
                    {
                        var b64 = part["md5=".Length..].Trim();
                        try
                        {
                            serverMd5 ??= Convert.ToHexString(Convert.FromBase64String(b64)).ToLowerInvariant();
                        }
                        catch { }
                    }
                }
            }
        }

        // Extract filename
        var fileName = ResolveFileName(response, finalUrl);

        return new DownloadMetadata
        {
            OriginalUrl = originalUrl,
            FinalUrl = finalUrl,
            FileName = fileName,
            TotalBytes = totalBytes,
            AcceptsRanges = acceptsRanges,
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            HttpVersion = response.Version,
            ServerMd5 = serverMd5,
            ServerSha256 = serverSha256
        };
    }

    public static string ResolveFileName(HttpResponseMessage response, string fallbackUrl)
    {
        // 1. Content-Disposition: filename*="UTF-8''my%20file.zip"
        if (response.Content.Headers.ContentDisposition != null)
        {
            var cd = response.Content.Headers.ContentDisposition;
            if (!string.IsNullOrWhiteSpace(cd.FileNameStar))
            {
                return SanitizeFileName(cd.FileNameStar);
            }
            if (!string.IsNullOrWhiteSpace(cd.FileName))
            {
                return SanitizeFileName(cd.FileName.Trim('"'));
            }
        }

        // 2. Extract from URL path
        if (Uri.TryCreate(fallbackUrl, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            var candidate = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Contains('.'))
            {
                return SanitizeFileName(Uri.UnescapeDataString(candidate));
            }
        }

        return "download.bin";
    }

    public static string SanitizeFileName(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalids.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "download.bin" : sanitized;
    }

    private static void ApplyCustomHeaders(HttpRequestMessage request, Dictionary<string, string>? headers)
    {
        if (headers == null) return;
        foreach (var (k, v) in headers)
        {
            request.Headers.TryAddWithoutValidation(k, v);
        }
    }
}
