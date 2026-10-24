namespace WDM.Core.Models;

/// <summary>
/// User-configurable options for starting a download task.
/// </summary>
public sealed class DownloadOptions
{
    public required string Url { get; init; }
    public string? DestinationDirectory { get; init; }
    public string? CustomFileName { get; init; }
    public int Connections { get; init; } = 16;
    public long? MaxBytesPerSecond { get; init; }
    public Dictionary<string, string>? CustomHeaders { get; init; }
    public bool PreferHttp3 { get; init; } = true;
    public bool OverwriteExisting { get; init; } = false;
    public int BufferSizeBytes { get; init; } = 64 * 1024; // 64 KB buffer
}
