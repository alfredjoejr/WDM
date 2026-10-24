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

        // Check Content-Range header (e.g., "bytes 0-0/104857600")
        if (response.Content.Headers.ContentRange != null)
        {
            if (response.Content.Headers.ContentRange.Length.HasValue)
            {
                totalBytes = response.Content.Headers.ContentRange.Length.Value;
                acceptsRanges = true;
            }
        }

        // Check standard Content-Length
        if (!totalBytes.HasValue && response.Content.Headers.ContentLength.HasValue)
        {
            totalBytes = response.Content.Headers.ContentLength.Value;
        }

        // Check Accept-Ranges header
        if (response.Headers.AcceptRanges.Contains("bytes") || response.StatusCode == HttpStatusCode.PartialContent)
        {
            acceptsRanges = true;
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
            HttpVersion = response.Version
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
