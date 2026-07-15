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
/// </summary>
public sealed record BackfillResults(string CourtId, int Page = 1);
