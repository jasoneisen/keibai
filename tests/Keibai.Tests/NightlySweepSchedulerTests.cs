using Keibai.Core.Ingestion;
using Xunit;
using Job = Keibai.Core.Ingestion.NightlySweepScheduler.Job;

namespace Keibai.Tests;

/// <summary>
/// The scheduler's kill-switch gate: with ingestion disabled, no BIT-touching message is ever
/// enqueued (they used to dead-letter nightly at the rate-limit handler and, being durable, sat
/// armed to fire the moment ingestion was re-enabled), while store-only jobs still run.
/// </summary>
public class NightlySweepSchedulerTests
{
    [Fact]
    public void Sweep_publishes_SyncCourts_when_enabled()
    {
        var messages = NightlySweepScheduler.MessagesFor(Job.Sweep, ingestionEnabled: true);

        Assert.Single(messages);
        Assert.IsType<SyncCourts>(messages[0]);
    }

    [Fact]
    public void Sweep_publishes_nothing_when_disabled()
    {
        Assert.Empty(NightlySweepScheduler.MessagesFor(Job.Sweep, ingestionEnabled: false));
    }

    [Fact]
    public void PostSweep_publishes_all_four_jobs_when_enabled()
    {
        var messages = NightlySweepScheduler.MessagesFor(Job.PostSweep, ingestionEnabled: true);

        Assert.Equal(
            [typeof(ScheduleArchiveWork), typeof(ScheduleResultsSync), typeof(RebuildDerivedDocuments), typeof(SummarizeSweep)],
            messages.Select(m => m.GetType()).ToArray());
    }

    [Fact]
    public void PostSweep_keeps_store_only_jobs_when_disabled()
    {
        var messages = NightlySweepScheduler.MessagesFor(Job.PostSweep, ingestionEnabled: false);

        Assert.Equal(
            [typeof(RebuildDerivedDocuments), typeof(SummarizeSweep)],
            messages.Select(m => m.GetType()).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Digest_runs_regardless_of_kill_switch(bool enabled)
    {
        var messages = NightlySweepScheduler.MessagesFor(Job.Digest, enabled);

        Assert.Single(messages);
        Assert.IsType<SendSavedSearchDigest>(messages[0]);
    }

    [Fact]
    public void ResultsEvening_publishes_ScheduleResultsSync_when_enabled()
    {
        var messages = NightlySweepScheduler.MessagesFor(Job.ResultsEvening, ingestionEnabled: true);

        Assert.Single(messages);
        Assert.IsType<ScheduleResultsSync>(messages[0]);
    }

    [Fact]
    public void ResultsEvening_publishes_nothing_when_disabled()
    {
        Assert.Empty(NightlySweepScheduler.MessagesFor(Job.ResultsEvening, ingestionEnabled: false));
    }
}
