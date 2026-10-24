using System.Text.Json;
using WDM.Core.Models;

namespace WDM.Core.Storage;

public sealed class CheckpointState
{
    public required string OriginalUrl { get; set; }
    public required string FinalUrl { get; set; }
    public required string FilePath { get; set; }
    public long? TotalBytes { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public List<DownloadChunk> Chunks { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Persists and restores download state for seamless pause/resume and crash recovery.
/// </summary>
public static class CheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string GetCheckpointPath(string destinationFilePath)
    {
        return destinationFilePath + ".wdm-state";
    }

    public static async Task SaveAsync(CheckpointState state, CancellationToken cancellationToken = default)
    {
        state.UpdatedAt = DateTimeOffset.UtcNow;
        var path = GetCheckpointPath(state.FilePath);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public static async Task<CheckpointState?> TryLoadAsync(string destinationFilePath, CancellationToken cancellationToken = default)
    {
        var path = GetCheckpointPath(destinationFilePath);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<CheckpointState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void DeleteCheckpoint(string destinationFilePath)
    {
        var path = GetCheckpointPath(destinationFilePath);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
