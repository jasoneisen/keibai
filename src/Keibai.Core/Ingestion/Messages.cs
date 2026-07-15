namespace Keibai.Core.Ingestion;

/// <summary>Kick off a nationwide sweep: enqueue a prefecture sync for all 47 prefectures.</summary>
public sealed record SyncCourts;

/// <summary>Sync one prefecture's active listings (discovers its courts organically from the rows).</summary>
public sealed record SyncPrefectureListings(string PrefectureId);

/// <summary>Fetch and upsert a single property's detail (idempotent on <c>{CourtId}:{SaleUnitId}</c>).</summary>
public sealed record SyncPropertyDetail(
    string PrefectureId, string CourtId, string SaleUnitId, string ResultsHtmlBlobPath);

// --- Phase 2: document archive ---

/// <summary>
/// Archive one property's 3点セット PDF (the archival core: BIT deletes these when bidding ends).
/// Idempotent — content-addressed, so a re-run of an already-archived property is a no-op.
/// </summary>
public sealed record ArchiveDocuments(string CourtId, string SaleUnitId);

/// <summary>
/// Re-check an already-archived property's 3点セット once mid-window for amendments; a differing hash is
/// archived as a new version (both kept).
/// </summary>
public sealed record RecheckDocuments(string CourtId, string SaleUnitId);

/// <summary>
/// Nightly reconciliation: enqueue <see cref="ArchiveDocuments"/> for eligible un-archived in-window
/// properties (soonest 入札期間 deadline first — the deletion-priority rule) and
/// <see cref="RecheckDocuments"/> for those due a mid-window re-check. Also the manual "drain the archive
/// backlog" trigger.
/// </summary>
public sealed record ScheduleArchiveWork;

// --- Phase 2: sale results (売却結果) ---

/// <summary>
/// Sync the 売却結果 for one court's round opening on <paramref name="OpeningDate"/> (scheduled for the
/// evening of 開札, after BIT publishes ~15:00–16:00 JST). Upserts <see cref="Domain.SaleResult"/>.
/// </summary>
public sealed record SyncRoundResults(string CourtId, DateOnly OpeningDate);

/// <summary>
/// One chunk of a court's historical-results backfill: page <paramref name="Page"/> of that court's
/// 売却結果. Each handler does ONE page then enqueues the next (per-court/per-page chunking keeps every
/// handler well under the 30-minute execution ceiling). BIT retains ~3 years of results.
/// <paramref name="PreviousResultsBlobPath"/> carries page N-1's HTML forward so the pager can replay its
/// <c>resultDetailForm</c> (null on page 1, which fetches fresh).
/// </summary>
public sealed record BackfillResults(string CourtId, int Page = 1, string? PreviousResultsBlobPath = null);

/// <summary>Fan out the nationwide results backfill: enqueue a page-1 <see cref="BackfillResults"/> per known court.</summary>
public sealed record BackfillAllResults;

/// <summary>
/// Nightly: enqueue <see cref="SyncRoundResults"/> for every court whose tracked properties had a 開札
/// in the last couple of days (results are published that evening). Idempotent upserts make re-runs safe.
/// </summary>
public sealed record ScheduleResultsSync;

// --- Phase 2: monitoring ---

/// <summary>
/// Summarize the latest nightly run against trailing norms and raise actionable alerts (fetch failures,
/// &gt;50% listing drops, high PDF-archive failure rate, zero data nationwide, storage over threshold).
/// Non-BIT work, so it does NOT ride the sequential ingestion queue. Also the manual "check health now"
/// trigger.
/// </summary>
public sealed record SummarizeSweep;
