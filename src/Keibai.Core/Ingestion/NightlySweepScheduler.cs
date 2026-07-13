using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Keibai.Core.Ingestion;

/// <summary>
/// Enqueues a nationwide <see cref="SyncCourts"/> sweep at 01:00 JST each night (BIT night hours). A
/// slim <see cref="BackgroundService"/> that only enqueues the message; all real work happens in the
/// Wolverine handlers. Gated by the same kill-switch as the client (a disabled ingestion never sweeps).
/// </summary>
public sealed class NightlySweepScheduler(
    IMessageBus bus,
    TimeProvider time,
    ILogger<NightlySweepScheduler> log) : BackgroundService
{
    private static readonly TimeZoneInfo Jst =
        TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNext0100Jst();
            log.LogInformation("Next nationwide sweep in {Delay} (01:00 JST).", delay);
            try
            {
                await time.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await bus.PublishAsync(new SyncCourts()).ConfigureAwait(false);
        }
    }

    private TimeSpan TimeUntilNext0100Jst()
    {
        var nowJst = TimeZoneInfo.ConvertTime(time.GetUtcNow(), Jst);
        var next = new DateTimeOffset(nowJst.Year, nowJst.Month, nowJst.Day, 1, 0, 0, nowJst.Offset);
        if (nowJst >= next)
        {
            next = next.AddDays(1);
        }

        return next - nowJst;
    }
}
