using Keibai.Core.Alerting;

namespace Keibai.Web.Reading;

/// <summary>Read-side accessor for the ops dashboard (<c>/jp/ops</c>) — "healthy in 30 seconds".</summary>
public interface IOpsReader
{
    /// <summary>One composite snapshot of system health.</summary>
    Task<OpsSnapshot> GetAsync(CancellationToken ct = default);
}

/// <summary>Everything the ops dashboard renders in one read.</summary>
/// <param name="StorageBytes">Total blob-store size.</param>
/// <param name="StorageGb">Same, in GB.</param>
/// <param name="MaxGigabytes">Storage alert threshold (0 disables).</param>
/// <param name="CourtsTotal">Known courts.</param>
/// <param name="DisabledCourts">Courts with crawling auto-disabled (a block was hit).</param>
/// <param name="Prefectures">Latest-sweep health per prefecture (with a small history sparkline).</param>
/// <param name="Today">Today's archive/results activity.</param>
/// <param name="QueueDepth">Pending durable envelopes (best-effort; null when unavailable).</param>
/// <param name="RecentAlerts">The most recent persisted alerts, newest first.</param>
public sealed record OpsSnapshot(
    long StorageBytes,
    double StorageGb,
    double MaxGigabytes,
    int CourtsTotal,
    IReadOnlyList<DisabledCourt> DisabledCourts,
    IReadOnlyList<PrefectureHealth> Prefectures,
    DailyStatsView Today,
    long? QueueDepth,
    IReadOnlyList<AlertRow> RecentAlerts);

/// <summary>A court whose crawling is auto-disabled.</summary>
public sealed record DisabledCourt(string Id, string Name, string? Reason, DateTimeOffset? At);

/// <summary>One prefecture's latest-sweep health line.</summary>
/// <param name="PrefectureId">BIT prefecture code.</param>
/// <param name="PrefectureName">Japanese name.</param>
/// <param name="LastSweepAt">When the latest sweep ran (null = never).</param>
/// <param name="ItemsFound">Listings found in the latest sweep.</param>
/// <param name="ItemsNew">New listings in the latest sweep.</param>
/// <param name="Errors">Errors after retries in the latest sweep.</param>
/// <param name="Blocked">Whether the latest sweep hit a block.</param>
/// <param name="Health">Traffic-light: <c>green</c> / <c>amber</c> / <c>red</c>.</param>
/// <param name="History">Recent sweeps' ItemsFound (oldest→newest) for a sparkline.</param>
public sealed record PrefectureHealth(
    string PrefectureId,
    string PrefectureName,
    DateTimeOffset? LastSweepAt,
    int ItemsFound,
    int ItemsNew,
    int Errors,
    bool Blocked,
    string Health,
    IReadOnlyList<int> History);

/// <summary>Today's (JST) archive + results activity, from <c>DailyStats</c>.</summary>
public sealed record DailyStatsView(
    string Date,
    int ArchiveAttempts,
    int PdfsArchived,
    int ArchiveFailures,
    int SaleResultsUpserted,
    int RechecksPerformed);

/// <summary>A persisted alert row.</summary>
public sealed record AlertRow(string Title, string Body, AlertSeverity Severity, DateTimeOffset At);
