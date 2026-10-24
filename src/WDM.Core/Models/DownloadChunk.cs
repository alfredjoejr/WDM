namespace WDM.Core.Models;

public enum ChunkStatus
{
    Pending,
    Downloading,
    Completed,
    Failed
}

/// <summary>
/// Represents an individual byte-range chunk allocated to a worker thread.
/// </summary>
public sealed class DownloadChunk
{
    public int Index { get; init; }
    public long StartOffset { get; init; }
    public long EndOffset { get; init; }
    public long BytesDownloaded { get; set; }
    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;

    public long TotalBytes => EndOffset - StartOffset + 1;
    public long RemainingBytes => Math.Max(0, TotalBytes - BytesDownloaded);
    public long CurrentOffset => StartOffset + BytesDownloaded;
    public double ProgressPercentage => TotalBytes > 0 
        ? Math.Clamp((double)BytesDownloaded / TotalBytes * 100.0, 0.0, 100.0) 
        : 0.0;
    public bool IsCompleted => BytesDownloaded >= TotalBytes;
}
