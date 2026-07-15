using Keibai.Core.Alerting;

namespace Keibai.Core.Monitoring;

/// <summary>One prefecture's line in a nightly-sweep snapshot (the grain the sweep actually runs at).</summary>
/// <param name="PrefectureId">JIS / BIT prefecture code.</param>
/// <param name="ItemsFound">Listings found this sweep.</param>
/// <param name="ItemsNew">Newly-created listings this sweep.</param>
/// <param name="Errors">Errors after retries (parked work).</param>
/// <param name="Blocked">True when BIT returned a 403/429/block-page for this prefecture.</param>
/// <param name="PreviousNonZeroItemsFound">Items found on the previous non-empty sweep, for drop detection.</param>
public sealed record PrefectureSweep(
    string PrefectureId,
    int ItemsFound,
    int ItemsNew,
    int Errors,
    bool Blocked,
    int? PreviousNonZeroItemsFound);

/// <summary>Everything the monitor needs to judge a nightly run — assembled by the handler, analyzed purely.</summary>
/// <param name="Prefectures">Per-prefecture results of the latest sweep.</param>
/// <param name="ArchiveAttempts">Archive attempts today.</param>
/// <param name="ArchiveFailures">Archive failures today.</param>
/// <param name="StorageBytes">Total blob-store size.</param>
/// <param name="MaxGigabytes">Storage alert threshold (≤0 disables).</param>
public sealed record MonitorSnapshot(
    IReadOnlyList<PrefectureSweep> Prefectures,
    int ArchiveAttempts,
    int ArchiveFailures,
    long StorageBytes,
    double MaxGigabytes);

/// <summary>
/// Turns a nightly-sweep <see cref="MonitorSnapshot"/> into a set of actionable alerts. Pure and
/// side-effect-free so the anomaly rules are unit-testable in isolation. "No news is good news" — an
/// empty result means the run was healthy and the operator hears nothing.
/// </summary>
public static class NightlyRunMonitor
{
    /// <summary>Archive-failure rate above this (with enough attempts to be meaningful) alerts.</summary>
    private const double ArchiveFailureRateThreshold = 0.05;

    /// <summary>Don't judge the failure rate until at least this many attempts (avoids 1/1 noise).</summary>
    private const int MinArchiveAttempts = 20;

    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    /// <summary>Analyze a snapshot, returning zero or more actionable alerts.</summary>
    public static IReadOnlyList<Alert> Analyze(MonitorSnapshot snapshot)
    {
        var alerts = new List<Alert>();

        foreach (var p in snapshot.Prefectures)
        {
            if (p.Blocked)
            {
                alerts.Add(new Alert(
                    $"BIT block during prefecture {p.PrefectureId}",
                    $"Prefecture {p.PrefectureId} hit a 403/429/block-page. Crawling for the affected court is "
                    + "auto-disabled. Do NOT retry around it — investigate (UA/IP/rate) before re-enabling.",
                    AlertSeverity.Critical));
            }
            else if (p.Errors > 0)
            {
                alerts.Add(new Alert(
                    $"Prefecture {p.PrefectureId} sweep had errors",
                    $"{p.Errors} error(s) after retries; the work item was parked. "
                    + $"Re-run when ready: POST /sync/prefecture/{p.PrefectureId}",
                    AlertSeverity.Warning));
            }

            if (p.PreviousNonZeroItemsFound is int prev && prev > 0 && 2 * p.ItemsFound < prev)
            {
                alerts.Add(new Alert(
                    $"Prefecture {p.PrefectureId} listings dropped >50%",
                    $"Was {prev}, now {p.ItemsFound}. Could be a takedown, a site change, or a parser regression. "
                    + "Verify against BIT before trusting the drop.",
                    AlertSeverity.Warning));
            }
        }

        // Zero listings ANYWHERE nationwide = the sweep almost certainly broke silently (a healthy sweep
        // always finds ~1,100 active properties). Zero *new* on its own is a normal quiet night, not an alert.
        if (snapshot.Prefectures.Count > 0 && snapshot.Prefectures.Sum(p => p.ItemsFound) == 0)
        {
            alerts.Add(new Alert(
                "Nationwide sweep found zero listings",
                "Every prefecture returned zero items. This is almost certainly a silent breakage "
                + "(auth/endpoint/parser), not a real empty result. Check the last CrawlRuns and BIT by hand.",
                AlertSeverity.Critical));
        }

        if (snapshot.ArchiveAttempts >= MinArchiveAttempts)
        {
            var rate = (double)snapshot.ArchiveFailures / snapshot.ArchiveAttempts;
            if (rate > ArchiveFailureRateThreshold)
            {
                alerts.Add(new Alert(
                    "PDF archive failure rate high",
                    $"{snapshot.ArchiveFailures}/{snapshot.ArchiveAttempts} archives failed "
                    + $"({rate:P0}). Check disk space, network, and whether BIT changed the download flow.",
                    AlertSeverity.Warning));
            }
        }

        if (snapshot.MaxGigabytes > 0 && snapshot.StorageBytes > snapshot.MaxGigabytes * BytesPerGigabyte)
        {
            var gb = snapshot.StorageBytes / (double)BytesPerGigabyte;
            alerts.Add(new Alert(
                "Blob storage over threshold",
                $"Blob store is {gb:N1} GB, over the {snapshot.MaxGigabytes:N0} GB limit. Add disk, prune old "
                + "captures, or narrow Keibai:Ingestion:ArchivePrefectures.",
                AlertSeverity.Warning));
        }

        return alerts;
    }
}
