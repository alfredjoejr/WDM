using WDM.Core.Models;

namespace WDM.Core.Engine;

public interface IDownloadEngine
{
    DownloadStatus Status { get; }
    DownloadMetadata? Metadata { get; }
    IReadOnlyList<DownloadChunk> Chunks { get; }

    event Action<DownloadProgressSnapshot>? ProgressChanged;
    event Action<DownloadStatus>? StatusChanged;
    event Action<string>? LogMessage;

    Task<string> StartAsync(DownloadOptions options, CancellationToken cancellationToken = default);
    void Pause();
    void Cancel();
}
