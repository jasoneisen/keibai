using Microsoft.Extensions.Options;

namespace Keibai.Core.Bit;

/// <summary>
/// The single global rate gate for BIT. Serialises every outbound request through one lock and
/// guarantees at least <see cref="BitOptions.MinRequestInterval"/> between the *start* of consecutive
/// requests, single-threaded, regardless of how many callers exist. This is the ONE place the 1-req/3s
/// rule is enforced (via <see cref="BitRateLimitingHandler"/>).
/// </summary>
public sealed class BitRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _interval;
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;

    /// <summary>Create a limiter bound to live options (so interval changes take effect).</summary>
    public BitRateLimiter(IOptionsMonitor<BitOptions> options, TimeProvider time)
    {
        _time = time;
        _interval = () => options.CurrentValue.MinRequestInterval;
    }

    /// <summary>Test-friendly constructor with a fixed interval.</summary>
    internal BitRateLimiter(TimeSpan interval, TimeProvider time)
    {
        _time = time;
        _interval = () => interval;
    }

    /// <summary>
    /// Block until it is permissible to start the next request, then record the start time. Serialised:
    /// only one caller proceeds at a time, so no parallel fetching against BIT is ever possible.
    /// </summary>
    public async Task AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var earliest = _lastStart + _interval();
            if (now < earliest)
            {
                var delay = earliest - now;
                await _time.Delay(delay, ct).ConfigureAwait(false);
            }

            _lastStart = _time.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();
}
