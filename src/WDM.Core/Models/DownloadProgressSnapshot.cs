namespace WDM.Core.Models;

/// <summary>
/// Immutable real-time snapshot of download progress for UI and CLI listeners.
/// </summary>
public sealed class DownloadProgressSnapshot
{
    public required DownloadStatus Status { get; init; }
    public long? TotalBytes { get; init; }
    public required long DownloadedBytes { get; init; }
    public double ProgressPercentage { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public TimeSpan? EstimatedRemainingTime { get; init; }
    public int ActiveConnections { get; init; }
    public required IReadOnlyList<DownloadChunk> Chunks { get; init; }

    public string SpeedFormatted => FormatBytes(SpeedBytesPerSecond) + "/s";
    public string DownloadedFormatted => FormatBytes(DownloadedBytes);
    public string TotalFormatted => TotalBytes.HasValue ? FormatBytes(TotalBytes.Value) : "Unknown";

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unitIndex = 0;
        while (bytes >= 1024.0 && unitIndex < units.Length - 1)
        {
            bytes /= 1024.0;
            unitIndex++;
        }
        return $"{bytes:0.##} {units[unitIndex]}";
    }
}
