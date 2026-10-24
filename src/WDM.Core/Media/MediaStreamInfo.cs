namespace WDM.Core.Media;

public sealed class MediaFormatOption
{
    public required string FormatId { get; init; }
    public required string Resolution { get; init; } // e.g. "1920x1080", "1280x720", "Audio Only"
    public string? Extension { get; init; } // "mp4", "webm", "m4a", "mp3"
    public long? FileSizeBytes { get; init; }
    public string? Note { get; init; }
    public bool IsAudioOnly { get; init; }
}

public sealed class MediaMetadataResult
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string? Uploader { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
    public List<MediaFormatOption> Formats { get; init; } = [];
}
