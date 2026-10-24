using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using WDM.Core.Models;
using WDM.Core.Network;
using WDM.Core.Storage;

namespace WDM.Core.Engine;

/// <summary>
/// High-performance multi-connection download engine with HTTP/3 support,
/// zero-stitch direct concurrent disk writes, checkpoint persistence, and dynamic chunk resumption.
/// </summary>
public sealed class DownloadEngine : IDownloadEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpProbe _probe;
    private readonly object _stateLock = new();

    private DownloadStatus _status = DownloadStatus.Idle;
    private DownloadMetadata? _metadata;
    private List<DownloadChunk> _chunks = [];
    private CancellationTokenSource? _cts;
    private DiskManager? _diskManager;
    private BandwidthThrottler? _throttler;
    private string? _destinationPath;

    public DownloadStatus Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                StatusChanged?.Invoke(_status);
            }
        }
    }

    public DownloadMetadata? Metadata => _metadata;
    public IReadOnlyList<DownloadChunk> Chunks => _chunks;

    public event Action<DownloadProgressSnapshot>? ProgressChanged;
    public event Action<DownloadStatus>? StatusChanged;
    public event Action<string>? LogMessage;

    public DownloadEngine(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HttpClientFactory.CreateClient();
        _probe = new HttpProbe(_httpClient);
    }

    public async Task<string> StartAsync(DownloadOptions options, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        try
        {
            // 1. Pre-flight probe
            Status = DownloadStatus.Probing;
            LogMessage?.Invoke($"Inspecting target URL: {options.Url}");
            _metadata = await _probe.ProbeAsync(options.Url, options.CustomHeaders, ct);

            LogMessage?.Invoke($"Detected file: {_metadata.FileName} ({(_metadata.TotalBytes.HasValue ? DownloadProgressSnapshot.FormatBytes(_metadata.TotalBytes.Value) : "Unknown size")})");
            LogMessage?.Invoke($"Range requests supported: {_metadata.AcceptsRanges} (HTTP/{_metadata.HttpVersion})");

            // 2. Resolve destination path
            var downloadDir = !string.IsNullOrWhiteSpace(options.DestinationDirectory)
                ? options.DestinationDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            var fileName = !string.IsNullOrWhiteSpace(options.CustomFileName)
                ? HttpProbe.SanitizeFileName(options.CustomFileName)
                : _metadata.FileName;

            _destinationPath = Path.Combine(downloadDir, fileName);

            // Handle file naming collisions if not overwriting
            if (!options.OverwriteExisting && File.Exists(_destinationPath))
            {
                var checkpoint = await CheckpointStore.TryLoadAsync(_destinationPath, ct);
                if (checkpoint == null)
                {
                    _destinationPath = GenerateUniqueFilePath(downloadDir, fileName);
                }
            }

            // 3. Setup bandwidth throttler if requested
            if (options.MaxBytesPerSecond.HasValue && options.MaxBytesPerSecond.Value > 0)
            {
                _throttler = new BandwidthThrottler(options.MaxBytesPerSecond.Value);
            }

            // 4. Setup chunks and checkpointing
            var existingCheckpoint = await CheckpointStore.TryLoadAsync(_destinationPath, ct);
            if (existingCheckpoint != null && CanResumeFrom(existingCheckpoint, _metadata))
            {
                LogMessage?.Invoke($"Resuming previous download checkpoint: {existingCheckpoint.Chunks.Count} chunks found.");
                _chunks = existingCheckpoint.Chunks;
            }
            else
            {
                _chunks = PlanChunks(_metadata.TotalBytes, _metadata.AcceptsRanges ? options.Connections : 1);
            }

            // 5. Pre-allocate disk space and open concurrent zero-stitch file handle
            Status = DownloadStatus.AllocatingDisk;
            LogMessage?.Invoke($"Allocating storage for: {_destinationPath}");
            _diskManager = DiskManager.Create(_destinationPath, _metadata.TotalBytes);

            // 6. Start multi-worker parallel download
            Status = DownloadStatus.Downloading;
            LogMessage?.Invoke($"Starting {_chunks.Count} concurrent worker streams...");

            using var progressCts = new CancellationTokenSource();
            var progressTask = RunProgressReporterAsync(progressCts.Token);

            var workerTasks = _chunks.Select(chunk => RunChunkWorkerAsync(chunk, options, ct)).ToArray();
            await Task.WhenAll(workerTasks);

            // Cancel progress loop and flush remaining buffers
            progressCts.Cancel();
            await progressTask;

            _diskManager.Flush();
            _diskManager.Dispose();
            _diskManager = null;

            // 7. Cleanup checkpoint upon success
            CheckpointStore.DeleteCheckpoint(_destinationPath);
            Status = DownloadStatus.Completed;
            LogMessage?.Invoke($"Download finished successfully: {_destinationPath}");

            // Final progress notification
            EmitProgress(0, 0, Status);

            return _destinationPath;
        }
        catch (OperationCanceledException)
        {
            if (Status == DownloadStatus.Paused)
            {
                LogMessage?.Invoke("Download paused.");
            }
            else
            {
                Status = DownloadStatus.Cancelled;
                LogMessage?.Invoke("Download cancelled by user.");
            }
            await SaveCurrentCheckpointAsync();
            throw;
        }
        catch (Exception ex)
        {
            Status = DownloadStatus.Failed;
            LogMessage?.Invoke($"Download error: {ex.Message}");
            await SaveCurrentCheckpointAsync();
            throw;
        }
    }

    private async Task RunChunkWorkerAsync(DownloadChunk chunk, DownloadOptions options, CancellationToken ct)
    {
        if (chunk.IsCompleted)
        {
            chunk.Status = ChunkStatus.Completed;
            return;
        }

        chunk.Status = ChunkStatus.Downloading;
        const int maxRetries = 5;
        int retryCount = 0;

        while (chunk.RemainingBytes > 0 && !ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _metadata!.FinalUrl);
                
                // Only send Range if range is supported and total length is known
                if (_metadata.AcceptsRanges && _metadata.TotalBytes.HasValue)
                {
                    request.Headers.Range = new RangeHeaderValue(chunk.CurrentOffset, chunk.EndOffset);
                }

                if (options.CustomHeaders != null)
                {
                    foreach (var (k, v) in options.CustomHeaders)
                    {
                        request.Headers.TryAddWithoutValidation(k, v);
                    }
                }

                using var response = await _httpClient.SendAsync(
                    request, 
                    HttpCompletionOption.ResponseHeadersRead, 
                    ct);

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var bufferSize = Math.Min(options.BufferSizeBytes, 128 * 1024);
                byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);

                try
                {
                    while (chunk.RemainingBytes > 0 && !ct.IsCancellationRequested)
                    {
                        int bytesToRead = (int)Math.Min(rentedBuffer.Length, chunk.RemainingBytes);
                        int bytesRead = await stream.ReadAsync(rentedBuffer.AsMemory(0, bytesToRead), ct);

                        if (bytesRead == 0) break; // EOF

                        // Zero-stitch direct write into final file at target offset
                        await _diskManager!.WriteAtAsync(
                            rentedBuffer.AsMemory(0, bytesRead), 
                            chunk.CurrentOffset, 
                            ct);

                        chunk.BytesDownloaded += bytesRead;

                        if (_throttler != null)
                        {
                            await _throttler.ThrottleAsync(bytesRead, ct);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }

                if (chunk.IsCompleted)
                {
                    chunk.Status = ChunkStatus.Completed;
                    return;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested && ++retryCount <= maxRetries)
            {
                chunk.Status = ChunkStatus.Failed;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)), ct);
                chunk.Status = ChunkStatus.Downloading;
            }
        }

        if (chunk.IsCompleted)
        {
            chunk.Status = ChunkStatus.Completed;
        }
    }

    private async Task RunProgressReporterAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        long lastDownloaded = 0;
        var lastTime = stopwatch.Elapsed;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var currentTime = stopwatch.Elapsed;
            var timeDelta = (currentTime - lastTime).TotalSeconds;
            if (timeDelta <= 0.05) continue;

            long currentDownloaded = _chunks.Sum(c => c.BytesDownloaded);
            long bytesDelta = currentDownloaded - lastDownloaded;
            double speed = Math.Max(0, bytesDelta / timeDelta);

            lastDownloaded = currentDownloaded;
            lastTime = currentTime;

            EmitProgress(currentDownloaded, speed, Status);
        }
    }

    private void EmitProgress(long downloaded, double speed, DownloadStatus status)
    {
        long? total = _metadata?.TotalBytes;
        double percentage = total.HasValue && total.Value > 0
            ? Math.Clamp((double)downloaded / total.Value * 100.0, 0.0, 100.0)
            : 0.0;

        TimeSpan? eta = null;
        if (total.HasValue && speed > 1024)
        {
            var remaining = Math.Max(0, total.Value - downloaded);
            eta = TimeSpan.FromSeconds(remaining / speed);
        }

        var snapshot = new DownloadProgressSnapshot
        {
            Status = status,
            TotalBytes = total,
            DownloadedBytes = downloaded,
            ProgressPercentage = percentage,
            SpeedBytesPerSecond = speed,
            EstimatedRemainingTime = eta,
            ActiveConnections = _chunks.Count(c => c.Status == ChunkStatus.Downloading),
            Chunks = _chunks.ToList()
        };

        ProgressChanged?.Invoke(snapshot);
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            if (Status == DownloadStatus.Downloading)
            {
                Status = DownloadStatus.Paused;
                _cts?.Cancel();
            }
        }
    }

    public void Cancel()
    {
        lock (_stateLock)
        {
            Status = DownloadStatus.Cancelled;
            _cts?.Cancel();
        }
    }

    private async Task SaveCurrentCheckpointAsync()
    {
        if (_metadata != null && !string.IsNullOrWhiteSpace(_destinationPath) && _chunks.Count > 0)
        {
            var checkpoint = new CheckpointState
            {
                OriginalUrl = _metadata.OriginalUrl,
                FinalUrl = _metadata.FinalUrl,
                FilePath = _destinationPath,
                TotalBytes = _metadata.TotalBytes,
                ETag = _metadata.ETag,
                LastModified = _metadata.LastModified,
                Chunks = _chunks.ToList()
            };

            await CheckpointStore.SaveAsync(checkpoint);
        }
    }

    private static List<DownloadChunk> PlanChunks(long? totalBytes, int connectionCount)
    {
        var list = new List<DownloadChunk>();
        if (!totalBytes.HasValue || totalBytes.Value <= 0 || connectionCount <= 1)
        {
            list.Add(new DownloadChunk
            {
                Index = 0,
                StartOffset = 0,
                EndOffset = totalBytes.HasValue ? totalBytes.Value - 1 : long.MaxValue,
                BytesDownloaded = 0
            });
            return list;
        }

        long total = totalBytes.Value;
        long chunkSize = total / connectionCount;

        for (int i = 0; i < connectionCount; i++)
        {
            long start = i * chunkSize;
            long end = (i == connectionCount - 1) ? total - 1 : (i + 1) * chunkSize - 1;

            list.Add(new DownloadChunk
            {
                Index = i,
                StartOffset = start,
                EndOffset = end,
                BytesDownloaded = 0
            });
        }

        return list;
    }

    private static bool CanResumeFrom(CheckpointState checkpoint, DownloadMetadata metadata)
    {
        if (checkpoint.TotalBytes != metadata.TotalBytes) return false;
        if (!string.IsNullOrWhiteSpace(metadata.ETag) && checkpoint.ETag != metadata.ETag) return false;
        return true;
    }

    private static string GenerateUniqueFilePath(string directory, string originalFileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        var ext = Path.GetExtension(originalFileName);
        int counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{nameWithoutExt} ({counter++}){ext}");
        } while (File.Exists(candidate));

        return candidate;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _diskManager?.Dispose();
    }
}
