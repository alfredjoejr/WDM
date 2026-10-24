using Microsoft.Win32.SafeHandles;

namespace WDM.Core.Storage;

/// <summary>
/// Manages high-performance zero-stitch direct concurrent disk writes.
/// Uses RandomAccess with Windows overlapped I/O to allow multiple worker threads
/// to stream directly into their designated byte offsets in a single file without locking.
/// </summary>
public sealed class DiskManager : IAsyncDisposable, IDisposable
{
    private readonly string _filePath;
    private readonly SafeFileHandle _fileHandle;
    private bool _isDisposed;

    public string FilePath => _filePath;

    private DiskManager(string filePath, SafeFileHandle fileHandle)
    {
        _filePath = filePath;
        _fileHandle = fileHandle;
    }

    /// <summary>
    /// Creates or opens the target file and pre-allocates total disk space upfront.
    /// </summary>
    public static DiskManager Create(string destinationPath, long? totalBytesToPreallocate = null)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var handle = File.OpenHandle(
            destinationPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        if (totalBytesToPreallocate.HasValue && totalBytesToPreallocate.Value > 0)
        {
            // Pre-allocate file size upfront to prevent disk fragmentation
            RandomAccess.SetLength(handle, totalBytesToPreallocate.Value);
        }

        return new DiskManager(destinationPath, handle);
    }

    /// <summary>
    /// Writes a memory buffer directly to a specific byte offset without locking.
    /// Fully thread-safe for parallel non-overlapping byte ranges.
    /// </summary>
    public ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer, 
        long fileOffset, 
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return RandomAccess.WriteAsync(_fileHandle, buffer, fileOffset, cancellationToken);
    }

    /// <summary>
    /// Flushes unwritten OS buffers to physical storage.
    /// </summary>
    public void Flush()
    {
        if (!_isDisposed)
        {
            RandomAccess.FlushToDisk(_fileHandle);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _fileHandle.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
