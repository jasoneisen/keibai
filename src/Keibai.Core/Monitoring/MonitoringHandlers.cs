using Keibai.Core.Alerting;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keibai.Core.Monitoring;

/// <summary>
/// Assembles a <see cref="MonitorSnapshot"/> from Marten + the blob store and turns
/// <see cref="NightlyRunMonitor"/>'s verdict into alerts. This is the passivity requirement: after each
/// nightly run, no news means all good; every alert it sends is actionable.
/// </summary>
public static class MonitoringHandler
{
    /// <summary>Only prefectures swept within this window of the newest run count as "this sweep".</summary>
    private static readonly TimeSpan SweepWindow = TimeSpan.FromHours(24);

    /// <summary>Build the snapshot, analyze it, and dispatch any alerts.</summary>
    public static async Task Handle(
        SummarizeSweep _,
        IKeibaiStoreAccessor store,
        IDocumentBlobStore blobs,
        IAlerter alerter,
        IOptions<StorageOptions> storage,
        TimeProvider time,
        ILogger<MonitoringMarker> log,
        CancellationToken ct)
    {
        await using var session = store.QuerySession();

        var runs = await session.Query<CrawlRun>()
            .Where(r => r.PrefectureId != null && r.FinishedAt != null)
            .OrderByDescending(r => r.StartedAt)
            .Take(500)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var prefectures = BuildPrefectureSweeps(runs);

        var stats = await session.LoadAsync<DailyStats>(JstClock.TodayKey(time)).ConfigureAwait(false);
        var storageBytes = await blobs.GetTotalBytesAsync(ct).ConfigureAwait(false);

        var snapshot = new MonitorSnapshot(
            prefectures,
            stats?.ArchiveAttempts ?? 0,
            stats?.ArchiveFailures ?? 0,
            storageBytes,
            storage.Value.MaxGigabytes);

        var alerts = NightlyRunMonitor.Analyze(snapshot);
        foreach (var alert in alerts)
        {
            await alerter.SendAsync(alert, ct).ConfigureAwait(false);
        }

        log.LogInformation(
            "Nightly monitor: {Prefectures} prefectures in this sweep, {Alerts} alert(s), blob store {Gb:N1} GB.",
            prefectures.Count, alerts.Count, storageBytes / (1024.0 * 1024 * 1024));
    }

    /// <summary>
    /// Reduce the recent <see cref="CrawlRun"/> history to one <see cref="PrefectureSweep"/> per prefecture
    /// that was part of the most recent sweep, each carrying its previous non-zero baseline for drop
    /// detection.
    /// </summary>
    internal static IReadOnlyList<PrefectureSweep> BuildPrefectureSweeps(IReadOnlyList<CrawlRun> runsNewestFirst)
    {
        if (runsNewestFirst.Count == 0)
        {
            return [];
        }

        var cutoff = runsNewestFirst[0].StartedAt - SweepWindow;
        var result = new List<PrefectureSweep>();

        foreach (var group in runsNewestFirst.GroupBy(r => r.PrefectureId!))
        {
            var ordered = group.OrderByDescending(r => r.StartedAt).ToList();
            var latest = ordered[0];
            if (latest.StartedAt < cutoff)
            {
                continue; // this prefecture was not part of the latest sweep — don't re-alert stale runs
            }

            var previousNonZero = ordered.Skip(1).FirstOrDefault(r => r.ItemsFound > 0)?.ItemsFound;
            var blocked = latest.Notes.Any(n => n.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase));
            result.Add(new PrefectureSweep(
                group.Key, latest.ItemsFound, latest.ItemsNew, latest.Errors, blocked, previousNonZero));
        }

        return result;
    }
}

/// <summary>Marker for typed <c>ILogger</c> injection in the monitoring handler.</summary>
public sealed class MonitoringMarker;
