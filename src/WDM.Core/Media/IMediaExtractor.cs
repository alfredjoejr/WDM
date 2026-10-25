namespace WDM.Core.Media;

public interface IMediaExtractor
{
    bool IsSupported(string url);
    Task<bool> IsEngineAvailableAsync(CancellationToken cancellationToken = default);
    Task<MediaMetadataResult?> ExtractMetadataAsync(string url, CancellationToken cancellationToken = default);
    Task<string> DownloadMediaAsync(
        string url, 
        string? formatId = null, 
        string? destinationDirectory = null, 
        string? outputFileName = null,
        bool isAudioOnly = false,
        Action<double, string>? progressCallback = null,
        CancellationToken cancellationToken = default);
}
