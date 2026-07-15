using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Keibai.Core.Ingestion;

/// <summary>
/// Fires Keibai's daily jobs at fixed JST wall-clock times (BIT night hours). A slim
/// <see cref="BackgroundService"/> that only ENQUEUES messages — all real work happens in the Wolverine
/// handlers. Two daily jobs:
/// <list type="bullet">
/// <item><b>01:00 JST</b> — <see cref="SyncCourts"/>: the nationwide listing sweep (which archives new
/// discoveries same-night as their details are synced).</item>
/// <item><b>07:00 JST</b> — <see cref="ScheduleArchiveWork"/> (deadline-ordered archive backlog drain +
/// due re-checks) then <see cref="Monitoring.SummarizeSweep"/> (anomaly alerts + storage watchdog), by
/// which time the overnight sweep has normally finished.</item>
/// </list>
/// Gated by the same kill-switch as the client — a disabled ingestion never sweeps (the enqueued messages
/// simply no-op at the rate-limit handler), but the monitor still runs.
/// </summary>
public sealed class NightlySweepScheduler(
    IServiceProvider services,
    TimeProvider time,
    ILogger<NightlySweepScheduler> log) : BackgroundService
{
    private static readonly TimeOnly SweepAt = new(1, 0);
    private static readonly TimeOnly PostSweepAt = new(7, 0);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var (delay, job) = NextJob();
            log.LogInformation("Next scheduled job {Job} in {Delay} (at {At} JST).", job, delay, JobTime(job));
            try
            {
                await time.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await using var scope = services.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            switch (job)
            {
                case Job.Sweep:
                    await bus.PublishAsync(new SyncCourts()).ConfigureAwait(false);
                    break;
                case Job.PostSweep:
                    await bus.PublishAsync(new ScheduleArchiveWork()).ConfigureAwait(false);
                    await bus.PublishAsync(new SummarizeSweep()).ConfigureAwait(false);
                    break;
            }
        }
    }

    private (TimeSpan Delay, Job Job) NextJob()
    {
        var nowJst = JstClock.Now(time);
        var sweep = NextOccurrence(nowJst, SweepAt);
        var post = NextOccurrence(nowJst, PostSweepAt);
        return sweep <= post ? (sweep - nowJst, Job.Sweep) : (post - nowJst, Job.PostSweep);
    }

    private static DateTimeOffset NextOccurrence(DateTimeOffset nowJst, TimeOnly at)
    {
        var next = new DateTimeOffset(
            nowJst.Year, nowJst.Month, nowJst.Day, at.Hour, at.Minute, 0, nowJst.Offset);
        return nowJst < next ? next : next.AddDays(1);
    }

    private static TimeOnly JobTime(Job job) => job == Job.Sweep ? SweepAt : PostSweepAt;

    private enum Job
    {
        Sweep,
        PostSweep,
    }
}
