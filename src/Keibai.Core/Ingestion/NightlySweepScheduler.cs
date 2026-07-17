using Keibai.Core.Bit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// due re-checks), <see cref="ScheduleResultsSync"/> (a morning catch-up for any 開札 whose results were
/// missed the evening before), then <see cref="SummarizeSweep"/> (anomaly alerts + storage watchdog), by
/// which time the overnight sweep has normally finished.</item>
/// <item><b>08:00 JST</b> — <see cref="SendSavedSearchDigest"/>: the Phase 3 personalization digest, run
/// after the 07:00 rebuild so saved searches evaluate against fresh derived documents. Store-only.</item>
/// <item><b>18:00 JST</b> — <see cref="ScheduleResultsSync"/>: the PRIMARY results trigger, run the evening
/// of a 開札 after BIT publishes each round's 売却結果 (~15:00–16:00 JST).</item>
/// </list>
/// Gated by the ingestion kill-switch: when <see cref="BitOptions.Enabled"/> is false the BIT-touching
/// messages are never enqueued (the rate-limit handler's <see cref="IngestionDisabledException"/> stays
/// as the deep last-resort guard), while store-only jobs — derived-doc rebuild, sweep summary/monitor,
/// digest — still run.
/// </summary>
public sealed class NightlySweepScheduler(
    IServiceProvider services,
    TimeProvider time,
    IOptionsMonitor<BitOptions> ingestion,
    ILogger<NightlySweepScheduler> log) : BackgroundService
{
    private static readonly TimeOnly SweepAt = new(1, 0);
    private static readonly TimeOnly PostSweepAt = new(7, 0);
    private static readonly TimeOnly DigestAt = new(8, 0);
    private static readonly TimeOnly ResultsEveningAt = new(18, 0);

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
            var enabled = ingestion.CurrentValue.Enabled;
            var messages = MessagesFor(job, enabled);
            if (messages.Count < MessagesFor(job, ingestionEnabled: true).Count)
            {
                log.LogInformation(
                    "Ingestion kill-switch is off — skipped the BIT-touching messages for {Job}.", job);
            }

            foreach (var message in messages)
            {
                await bus.PublishAsync(message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The messages a job publishes. When the kill-switch is off, every message whose handler talks to
    /// BIT (sweep, archive backlog, results sync) is omitted — enqueuing it would only dead-letter at
    /// the rate-limit handler — while store-only work (derived rebuild, sweep summary, digest) remains.
    /// </summary>
    internal static IReadOnlyList<object> MessagesFor(Job job, bool ingestionEnabled) => job switch
    {
        Job.Sweep => ingestionEnabled ? [new SyncCourts()] : [],
        Job.PostSweep => ingestionEnabled
            ? [new ScheduleArchiveWork(), new ScheduleResultsSync(), new RebuildDerivedDocuments(), new SummarizeSweep()]
            : [new RebuildDerivedDocuments(), new SummarizeSweep()],
        Job.Digest => [new SendSavedSearchDigest()],
        Job.ResultsEvening => ingestionEnabled ? [new ScheduleResultsSync()] : [],
        _ => throw new ArgumentOutOfRangeException(nameof(job), job, null),
    };

    private (TimeSpan Delay, Job Job) NextJob()
    {
        var nowJst = JstClock.Now(time);
        var soonest = new[] { Job.Sweep, Job.PostSweep, Job.Digest, Job.ResultsEvening }
            .Select(j => (At: NextOccurrence(nowJst, JobTime(j)), Job: j))
            .OrderBy(x => x.At)
            .First();
        return (soonest.At - nowJst, soonest.Job);
    }

    private static DateTimeOffset NextOccurrence(DateTimeOffset nowJst, TimeOnly at)
    {
        var next = new DateTimeOffset(
            nowJst.Year, nowJst.Month, nowJst.Day, at.Hour, at.Minute, 0, nowJst.Offset);
        return nowJst < next ? next : next.AddDays(1);
    }

    private static TimeOnly JobTime(Job job) => job switch
    {
        Job.Sweep => SweepAt,
        Job.PostSweep => PostSweepAt,
        Job.Digest => DigestAt,
        Job.ResultsEvening => ResultsEveningAt,
        _ => throw new ArgumentOutOfRangeException(nameof(job), job, null),
    };

    internal enum Job
    {
        Sweep,
        PostSweep,
        Digest,
        ResultsEvening,
    }
}
