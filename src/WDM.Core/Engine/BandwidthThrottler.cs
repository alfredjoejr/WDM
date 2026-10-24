using System.Diagnostics;

namespace WDM.Core.Engine;

/// <summary>
/// Token-bucket bandwidth limiter for setting global or per-download speed caps.
/// </summary>
public sealed class BandwidthThrottler
{
    private readonly long _bytesPerSecond;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _totalBytesAllowed;
    private long _totalBytesConsumed;
    private readonly object _lock = new();

    public BandwidthThrottler(long bytesPerSecond)
    {
        _bytesPerSecond = Math.Max(1024, bytesPerSecond);
    }

    public async ValueTask ThrottleAsync(int bytesToWrite, CancellationToken cancellationToken = default)
    {
        if (_bytesPerSecond <= 0) return;

        double delayMilliseconds = 0;

        lock (_lock)
        {
            _totalBytesConsumed += bytesToWrite;
            var elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
            _totalBytesAllowed = (long)(elapsedSeconds * _bytesPerSecond);

            if (_totalBytesConsumed > _totalBytesAllowed)
            {
                var excessBytes = _totalBytesConsumed - _totalBytesAllowed;
                delayMilliseconds = (double)excessBytes / _bytesPerSecond * 1000.0;
            }
        }

        if (delayMilliseconds > 2)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken);
        }
    }
}
