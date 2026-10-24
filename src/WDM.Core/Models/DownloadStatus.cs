namespace WDM.Core.Models;

/// <summary>
/// Status of an active or queued download task.
/// </summary>
public enum DownloadStatus
{
    Idle,
    Probing,
    AllocatingDisk,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}
