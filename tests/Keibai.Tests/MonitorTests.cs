using Keibai.Core.Alerting;
using Keibai.Core.Domain;
using Keibai.Core.Monitoring;
using Xunit;

namespace Keibai.Tests;

public class MonitorTests
{
    private static PrefectureSweep Ok(string id, int found = 40, int isNew = 3) =>
        new(id, found, isNew, Errors: 0, Blocked: false, PreviousNonZeroItemsFound: found);

    private static MonitorSnapshot Snapshot(
        IReadOnlyList<PrefectureSweep> prefectures,
        int attempts = 0, int failures = 0, long storageBytes = 0, double maxGb = 50) =>
        new(prefectures, attempts, failures, storageBytes, maxGb);

    [Fact]
    public void Healthy_run_produces_no_alerts()
    {
        var alerts = NightlyRunMonitor.Analyze(Snapshot([Ok("13"), Ok("27"), Ok("40")]));
        Assert.Empty(alerts);
    }

    [Fact]
    public void A_blocked_prefecture_is_a_critical_alert()
    {
        var blocked = new PrefectureSweep("13", 0, 0, Errors: 1, Blocked: true, PreviousNonZeroItemsFound: 42);
        var alerts = NightlyRunMonitor.Analyze(Snapshot([blocked, Ok("27")]));
        var alert = Assert.Single(alerts, a => a.Title.Contains("block"));
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public void Errors_after_retries_warn_but_are_not_double_counted_as_blocks()
    {
        var errored = new PrefectureSweep("13", 40, 2, Errors: 2, Blocked: false, PreviousNonZeroItemsFound: 42);
        var alerts = NightlyRunMonitor.Analyze(Snapshot([errored]));
        var alert = Assert.Single(alerts);
        Assert.Contains("errors", alert.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void Listing_count_dropping_more_than_half_warns()
    {
        var dropped = new PrefectureSweep("13", 20, 0, Errors: 0, Blocked: false, PreviousNonZeroItemsFound: 42);
        var alerts = NightlyRunMonitor.Analyze(Snapshot([dropped, Ok("27")]));
        Assert.Single(alerts, a => a.Title.Contains("dropped"));
    }

    [Fact]
    public void Exactly_half_is_not_a_drop_alert()
    {
        var half = new PrefectureSweep("13", 21, 0, Errors: 0, Blocked: false, PreviousNonZeroItemsFound: 42);
        var alerts = NightlyRunMonitor.Analyze(Snapshot([half]));
        Assert.DoesNotContain(alerts, a => a.Title.Contains("dropped"));
    }

    [Fact]
    public void Zero_listings_nationwide_is_critical()
    {
        var alerts = NightlyRunMonitor.Analyze(Snapshot(
        [
            new PrefectureSweep("13", 0, 0, 0, false, 42),
            new PrefectureSweep("27", 0, 0, 0, false, 30),
        ]));
        Assert.Single(alerts, a => a.Title.Contains("zero listings") && a.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public void Zero_new_but_nonzero_found_is_a_quiet_night_not_an_alert()
    {
        // No new listings, but the sweep still found the existing ones — normal, silent.
        var alerts = NightlyRunMonitor.Analyze(Snapshot(
        [
            new PrefectureSweep("13", 42, 0, 0, false, 42),
            new PrefectureSweep("27", 30, 0, 0, false, 30),
        ]));
        Assert.Empty(alerts);
    }

    [Fact]
    public void High_archive_failure_rate_warns_only_with_enough_attempts()
    {
        // 3/50 = 6% > 5% with a meaningful sample → alert.
        Assert.Single(
            NightlyRunMonitor.Analyze(Snapshot([Ok("13")], attempts: 50, failures: 3)),
            a => a.Title.Contains("archive failure"));

        // 1/1 = 100% but too few attempts to judge → no alert (avoids noise).
        Assert.DoesNotContain(
            NightlyRunMonitor.Analyze(Snapshot([Ok("13")], attempts: 1, failures: 1)),
            a => a.Title.Contains("archive failure"));
    }

    [Fact]
    public void Storage_over_threshold_warns_and_zero_threshold_disables_the_watchdog()
    {
        var overBytes = 21L * 1024 * 1024 * 1024; // 21 GB
        Assert.Single(
            NightlyRunMonitor.Analyze(Snapshot([Ok("13")], storageBytes: overBytes, maxGb: 20)),
            a => a.Title.Contains("storage", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            NightlyRunMonitor.Analyze(Snapshot([Ok("13")], storageBytes: overBytes, maxGb: 0)),
            a => a.Title.Contains("storage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_prefecture_sweeps_takes_latest_and_previous_nonzero_baseline()
    {
        var baseTime = DateTimeOffset.Parse("2026-07-15T01:30:00+09:00");
        var runs = new List<CrawlRun>
        {
            Run("13", baseTime, found: 20),                       // latest
            Run("13", baseTime.AddDays(-1), found: 42),           // previous non-zero baseline
            Run("13", baseTime.AddDays(-2), found: 0),            // skipped for baseline
            Run("27", baseTime.AddHours(-1), found: 30),          // in-window, different prefecture
            Run("40", baseTime.AddDays(-5), found: 15),           // stale → excluded from this sweep
        };

        var sweeps = MonitoringHandler.BuildPrefectureSweeps(runs);

        var tokyo = Assert.Single(sweeps, s => s.PrefectureId == "13");
        Assert.Equal(20, tokyo.ItemsFound);
        Assert.Equal(42, tokyo.PreviousNonZeroItemsFound);
        Assert.Contains(sweeps, s => s.PrefectureId == "27");
        Assert.DoesNotContain(sweeps, s => s.PrefectureId == "40"); // stale run excluded
    }

    private static CrawlRun Run(string prefecture, DateTimeOffset started, int found) => new()
    {
        Id = Guid.NewGuid(),
        PrefectureId = prefecture,
        StartedAt = started,
        FinishedAt = started.AddMinutes(5),
        ItemsFound = found,
        ItemsNew = 0,
    };
}
