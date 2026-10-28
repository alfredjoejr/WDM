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
    public long EndOffset { get; set; }
    public long BytesDownloaded { get; set; }
    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;

    public bool IsUnknownLength => EndOffset == long.MaxValue;
    public long TotalBytes => IsUnknownLength ? long.MaxValue : (EndOffset - StartOffset + 1);
    public long RemainingBytes => IsUnknownLength ? long.MaxValue : Math.Max(0, TotalBytes - BytesDownloaded);
    public long CurrentOffset => StartOffset + BytesDownloaded;
    public double ProgressPercentage => !IsUnknownLength && TotalBytes > 0 
        ? Math.Clamp((double)BytesDownloaded / TotalBytes * 100.0, 0.0, 100.0) 
        : (Status == ChunkStatus.Completed ? 100.0 : 0.0);
    public bool IsCompleted => Status == ChunkStatus.Completed || (!IsUnknownLength && BytesDownloaded >= TotalBytes);
}
