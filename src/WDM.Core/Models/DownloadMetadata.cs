namespace WDM.Core.Models;

/// <summary>
/// Server metadata discovered through pre-flight HTTP probe.
/// </summary>
public sealed class DownloadMetadata
{
    public required string OriginalUrl { get; init; }
    public required string FinalUrl { get; init; }
    public required string FileName { get; init; }
    public long? TotalBytes { get; init; }
    public bool AcceptsRanges { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? ContentType { get; init; }
    public Version? HttpVersion { get; init; }
}
