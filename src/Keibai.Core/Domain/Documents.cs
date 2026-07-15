namespace Keibai.Core.Domain;

/// <summary>Property sale-type classification (BIT <c>saleCls</c>).</summary>
public enum SaleCls
{
    /// <summary>土地 — land.</summary>
    Land = 1,
    /// <summary>戸建 — detached house.</summary>
    Detached = 2,
    /// <summary>マンション — condominium unit.</summary>
    Mansion = 3,
    /// <summary>その他 — other.</summary>
    Other = 4,
}

/// <summary>強制競売 (ヌ) vs 担保不動産競売 (ケ).</summary>
public enum CaseType
{
    /// <summary>Unknown / unparsed.</summary>
    Unknown = 0,
    /// <summary>担保不動産競売 — secured-property auction (ケ).</summary>
    Ke = 1,
    /// <summary>強制競売 — compulsory auction (ヌ).</summary>
    Nu = 2,
}

/// <summary>
/// A BIT court or branch. Id is the 5-digit BIT court code (e.g. <c>31111</c> 東京地方裁判所本庁,
/// <c>31131</c> 東京地方裁判所立川支部).
/// </summary>
public sealed class Court
{
    /// <summary>BIT court code (natural key).</summary>
    public required string Id { get; set; }
    /// <summary>Court name in Japanese.</summary>
    public required string Name { get; set; }
    /// <summary>JIS prefecture code 01–47.</summary>
    public required string PrefectureId { get; set; }
    /// <summary>True when the name contains 支部 (a branch, not a head office).</summary>
    public bool IsBranch { get; set; }
    /// <summary>When this court was first observed in a sweep.</summary>
    public DateTimeOffset FirstSeen { get; set; }
    /// <summary>When this court was last observed in a sweep.</summary>
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>
    /// When true, no BIT traffic is issued for this court (detail/archive/results handlers skip it). Set
    /// automatically when BIT returns a 403/429/block-page for the court (stop-and-alert), cleared by hand
    /// after investigation. A per-court complement to the global <c>Keibai:Ingestion:Enabled</c> switch.
    /// </summary>
    public bool CrawlDisabled { get; set; }
    /// <summary>Why crawling was disabled (block reason), if it was.</summary>
    public string? CrawlDisabledReason { get; set; }
    /// <summary>When crawling was disabled.</summary>
    public DateTimeOffset? CrawlDisabledAt { get; set; }
}

/// <summary>
/// A parsed case number, e.g. 令和08年(ヌ)第12号 → Era=令和, Year=8, Type=Nu, Serial=12.
/// </summary>
public sealed record CaseNumber(string Era, int Year, CaseType Type, int Serial, string Raw);

/// <summary>
/// A single auction property item (物件 / BIT "sale unit"). Natural key = <see cref="Id"/> which is
/// <c>{CourtId}:{SaleUnitId}</c> so re-ingestion is idempotent across courts.
/// </summary>
public sealed class PropertyItem
{
    /// <summary><c>{CourtId}:{SaleUnitId}</c> — the Marten identity.</summary>
    public required string Id { get; set; }
    /// <summary>BIT 11-digit sale-unit id.</summary>
    public required string SaleUnitId { get; set; }
    /// <summary>BIT court code.</summary>
    public required string CourtId { get; set; }
    /// <summary>JIS prefecture code the sweep drove this row from.</summary>
    public required string PrefectureId { get; set; }
    /// <summary>Court name as shown on the row.</summary>
    public string? CourtName { get; set; }
    /// <summary>Parsed case number (best-effort).</summary>
    public CaseNumber? Case { get; set; }
    /// <summary>Property type.</summary>
    public SaleCls? SaleCls { get; set; }
    /// <summary>Raw address as displayed (地番 or 住居表示).</summary>
    public string? RawAddress { get; set; }
    /// <summary>売却基準価額 (yen), where parsed.</summary>
    public long? SaleStandardAmount { get; set; }
    /// <summary>買受可能価額 (yen), where parsed.</summary>
    public long? MinimumBidAmount { get; set; }
    /// <summary>BIT-supplied latitude (best-effort, never trusted).</summary>
    public double? Latitude { get; set; }
    /// <summary>BIT-supplied longitude (best-effort, never trusted).</summary>
    public double? Longitude { get; set; }

    // --- Bidding schedule (from the detail page; drives archive priority + results scheduling) ---

    /// <summary>閲覧開始日 — the day the 3点セット becomes viewable.</summary>
    public DateOnly? ViewingStart { get; set; }
    /// <summary>入札期間 start — the day bidding opens.</summary>
    public DateOnly? BiddingStart { get; set; }
    /// <summary>
    /// 入札期間 end — the day bidding closes. The 3点セット PDFs are deleted around here, so this is the
    /// archive-priority key: properties whose <see cref="BiddingEnd"/> is soonest are archived first.
    /// </summary>
    public DateOnly? BiddingEnd { get; set; }
    /// <summary>開札期日 — the bid-opening day; 売却結果 is published ~15:00–16:00 JST this day.</summary>
    public DateOnly? OpeningDate { get; set; }
    /// <summary>売却決定期日 — the sale-decision day.</summary>
    public DateOnly? SaleDecisionDate { get; set; }

    // --- Detail attributes (Phase 2 enrichment; a card/sale-unit bundles one or more 物件) ---

    /// <summary>
    /// Every 物件 in this sale unit with its full attribute set (the generic label→value map lifted from
    /// the detail page's 物件明細 tables). Captures EVERYTHING BIT shows, per item, whether or not it is
    /// also promoted to a typed field below — so no attribute is ever lost.
    /// </summary>
    public List<PropertyItemDetail> Items { get; set; } = [];

    /// <summary>Number of 物件 bundled in this sale unit (<c>Items.Count</c>).</summary>
    public int ItemCount { get; set; }

    /// <summary>所在地 from the detail page (地番; may be more precise than the listing <see cref="RawAddress"/>).</summary>
    public string? DetailAddress { get; set; }

    // Typed rollups (best-effort, first non-empty across the bundled 物件) for search / indexing / profiling.

    /// <summary>家屋番号.</summary>
    public string? HouseNumber { get; set; }
    /// <summary>地目（登記）— land category.</summary>
    public string? LandCategory { get; set; }
    /// <summary>用途地域 — zoning.</summary>
    public string? ZoningUse { get; set; }
    /// <summary>建ぺい率 — building coverage ratio (as displayed, e.g. <c>60%</c>).</summary>
    public string? BuildingCoverageRatio { get; set; }
    /// <summary>容積率 — floor-area ratio (as displayed).</summary>
    public string? FloorAreaRatio { get; set; }
    /// <summary>構造（登記）— building structure.</summary>
    public string? Structure { get; set; }
    /// <summary>間取り — room layout.</summary>
    public string? RoomLayout { get; set; }
    /// <summary>敷地利用権 — site-use right (所有権 / 賃借権 …).</summary>
    public string? LandRights { get; set; }
    /// <summary>占有者 — occupancy status (role only, never a name; e.g. あり / 債務者・所有者).</summary>
    public string? Occupant { get; set; }
    /// <summary>築年月 — build year/month, as displayed (e.g. 平成29年6月).</summary>
    public string? BuildYearMonth { get; set; }
    /// <summary>Gregorian build year parsed from <see cref="BuildYearMonth"/>.</summary>
    public int? BuildYear { get; set; }
    /// <summary>階 — floor (マンション).</summary>
    public string? Floor { get; set; }
    /// <summary>総戸数 — total units in the building (マンション).</summary>
    public int? TotalUnits { get; set; }
    /// <summary>管理費等 (yen/month) — マンション.</summary>
    public long? AdminFeeYen { get; set; }
    /// <summary>土地面積（登記）in m² (best-effort).</summary>
    public double? LandAreaSqm { get; set; }
    /// <summary>床面積（登記）in m² (戸建; first floor / first value, best-effort).</summary>
    public double? BuildingAreaSqm { get; set; }
    /// <summary>専有面積（登記）in m² (マンション, best-effort).</summary>
    public double? ExclusiveAreaSqm { get; set; }

    // --- Archival state (Phase 2) ---

    /// <summary>When this property's 3点セット was last successfully archived (null = never).</summary>
    public DateTimeOffset? LastArchivedAt { get; set; }
    /// <summary>
    /// When the archive was last re-checked for amendments mid-window (null = never re-checked). Guards
    /// the once-per-window re-check so a property is not re-downloaded every sweep.
    /// </summary>
    public DateTimeOffset? LastRecheckedAt { get; set; }
    /// <summary>
    /// True when BIT's availability gate reported the 3点セット is no longer downloadable (bidding ended /
    /// deleted). Stops us from retrying a download that can never succeed.
    /// </summary>
    public bool ThreeSetUnavailable { get; set; }

    /// <summary>First time this item was observed.</summary>
    public DateTimeOffset FirstSeen { get; set; }
    /// <summary>Most recent time this item was observed.</summary>
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>Takedown-ready flag (private system, but modelled now).</summary>
    public bool Delisted { get; set; }
    /// <summary>Reason for delisting, if any.</summary>
    public string? DelistedReason { get; set; }
}

/// <summary>
/// One 物件 within a sale unit, with its complete attribute set from the detail page's 物件明細 table.
/// Kind is the per-item 種別 (土地 / 建物 / 区分所有建物), distinct from the card-level
/// <see cref="PropertyItem.SaleCls"/>. <see cref="Attributes"/> is the raw label→value map — every field
/// BIT renders — so nothing is lost even when it is not promoted to a typed rollup.
/// </summary>
public sealed class PropertyItemDetail
{
    /// <summary>物件番号.</summary>
    public string? ItemNo { get; set; }
    /// <summary>種別 (土地 / 建物 / 区分所有建物).</summary>
    public string? Kind { get; set; }
    /// <summary>Every attribute label → value from this 物件's detail table.</summary>
    public Dictionary<string, string> Attributes { get; set; } = [];
}

/// <summary>
/// A 競売 case (事件) — one court case that may bundle several 物件/sale units sold together or
/// separately. Materialized by grouping <see cref="PropertyItem"/>s on court + case number (BIT exposes
/// no case entity; the relationship is derived). Identity = <c>{CourtId}:{CaseLabel}</c>.
/// </summary>
public sealed class AuctionCase
{
    /// <summary><c>{CourtId}:{CaseLabel}</c> — the Marten identity.</summary>
    public required string Id { get; set; }
    /// <summary>BIT court code.</summary>
    public required string CourtId { get; set; }
    /// <summary>JIS prefecture code.</summary>
    public string? PrefectureId { get; set; }
    /// <summary>Parsed case number.</summary>
    public CaseNumber? Case { get; set; }
    /// <summary>Raw case label as displayed (令和07年(ケ)第476号).</summary>
    public string? CaseLabel { get; set; }
    /// <summary>The sale-unit <see cref="PropertyItem"/> ids in this case.</summary>
    public List<string> PropertyItemIds { get; set; } = [];
    /// <summary>Number of sale units in the case.</summary>
    public int PropertyCount { get; set; }
    /// <summary>When this case was last (re)built from the property store.</summary>
    public DateTimeOffset LastBuilt { get; set; }
}

/// <summary>
/// A bidding round (期間入札) at a court, identified by its 開札期日. Materialized by grouping
/// <see cref="PropertyItem"/>s on court + 開札期日 (BIT's <c>saleScdId</c> spans multiple 開札 dates, so
/// the clean round grain is the 開札 date). Identity = <c>{CourtId}:{OpeningDate:yyyy-MM-dd}</c>.
/// </summary>
public sealed class AuctionRound
{
    /// <summary><c>{CourtId}:{OpeningDate:yyyy-MM-dd}</c> — the Marten identity.</summary>
    public required string Id { get; set; }
    /// <summary>BIT court code.</summary>
    public required string CourtId { get; set; }
    /// <summary>JIS prefecture code.</summary>
    public string? PrefectureId { get; set; }
    /// <summary>開札期日 — the bid-opening day (the round key).</summary>
    public DateOnly OpeningDate { get; set; }
    /// <summary>閲覧開始日 — documents viewable from.</summary>
    public DateOnly? ViewingStart { get; set; }
    /// <summary>入札期間 start.</summary>
    public DateOnly? BiddingStart { get; set; }
    /// <summary>入札期間 end.</summary>
    public DateOnly? BiddingEnd { get; set; }
    /// <summary>売却決定期日.</summary>
    public DateOnly? SaleDecisionDate { get; set; }
    /// <summary>Sale-unit <see cref="PropertyItem"/> ids opening on this date at this court.</summary>
    public List<string> PropertyItemIds { get; set; } = [];
    /// <summary>Number of sale units in the round.</summary>
    public int PropertyCount { get; set; }
    /// <summary>Derived lifecycle: upcoming / viewing / bidding / closed / opened.</summary>
    public string Status { get; set; } = "unknown";
    /// <summary>When this round was last (re)built.</summary>
    public DateTimeOffset LastBuilt { get; set; }
}

/// <summary>
/// An archived 3点セット (or component) PDF. Identity is <c>{PropertyItemId}:{Sha256}</c>: idempotent
/// per property (re-downloading identical bytes maps to the same record) while still letting two
/// different properties each keep their own record even in the astronomically-unlikely case of identical
/// content. The blob itself is content-addressed by <see cref="Sha256"/> alone, so identical bytes dedupe
/// on disk. A mid-window amendment changes the bytes → a new sha → a NEW record with an incremented
/// <see cref="Version"/>; both are kept (never overwrite an earlier capture).
/// </summary>
public sealed class ArchivedDocument
{
    /// <summary><c>{PropertyItemId}:{Sha256}</c> — the Marten identity.</summary>
    public required string Id { get; set; }
    /// <summary>Owning property id (<c>{CourtId}:{SaleUnitId}</c>).</summary>
    public required string PropertyItemId { get; set; }
    /// <summary>sha256 hex of the bytes — the content address / blob key.</summary>
    public required string Sha256 { get; set; }
    /// <summary>Document kind: combined / 明細書 / 調査報告書 / 評価書 / 公告.</summary>
    public required string Kind { get; set; }
    /// <summary>1-based version for this property (increments when a re-check finds amended bytes).</summary>
    public int Version { get; set; } = 1;
    /// <summary>Byte size.</summary>
    public long ByteSize { get; set; }
    /// <summary>Source URL it was fetched from.</summary>
    public required string SourceUrl { get; set; }
    /// <summary>When fetched.</summary>
    public DateTimeOffset FetchedAt { get; set; }
    /// <summary>Blob path in the <c>IDocumentBlobStore</c>.</summary>
    public required string BlobPath { get; set; }
    /// <summary>Server-supplied filename, if any (advisory).</summary>
    public string? SuggestedFileName { get; set; }
}

/// <summary>Per-court per-run crawl statistics; powers monitoring.</summary>
public sealed class CrawlRun
{
    /// <summary>Marten identity (guid).</summary>
    public Guid Id { get; set; }
    /// <summary>Court this run swept (null = nationwide orchestration run).</summary>
    public string? CourtId { get; set; }
    /// <summary>Prefecture driving the sweep, if court-level.</summary>
    public string? PrefectureId { get; set; }
    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>When the run finished (null while in flight).</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Total BIT requests made during this run.</summary>
    public int RequestsMade { get; set; }
    /// <summary>Items found.</summary>
    public int ItemsFound { get; set; }
    /// <summary>Items newly created.</summary>
    public int ItemsNew { get; set; }
    /// <summary>Items whose fields changed.</summary>
    public int ItemsChanged { get; set; }
    /// <summary>Errors encountered.</summary>
    public int Errors { get; set; }
    /// <summary>Free-form notes (block-page detection, etc.).</summary>
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// 売却結果 — the outcome of a bidding round for a property. Natural-key identity (<see cref="Id"/> =
/// <see cref="MakeId"/>) so the nationwide backfill is idempotent: re-crawling a court's past results
/// upserts, never duplicates.
/// </summary>
public sealed class SaleResult
{
    /// <summary>Natural-key identity — see <see cref="MakeId"/> (<c>{CourtId}:{CaseLabel}:{ItemNo}</c>).</summary>
    public required string Id { get; set; }
    /// <summary>
    /// Owning property id (<c>{CourtId}:{SaleUnitId}</c>) when the property is also tracked. Null for
    /// historical backfill rows, whose property is long delisted (BIT's results list carries no sale-unit
    /// id to link on — best-effort matching by case number is a later concern).
    /// </summary>
    public string? PropertyItemId { get; set; }
    /// <summary>BIT court code.</summary>
    public string? CourtId { get; set; }
    /// <summary>物件番号 (item number within the case).</summary>
    public string? ItemNo { get; set; }
    /// <summary>Case number as displayed on the results row (raw).</summary>
    public string? CaseLabel { get; set; }
    /// <summary>開札 date, where the results view carries it (the full-history view does not).</summary>
    public DateOnly? OpeningDate { get; set; }
    /// <summary>売却価額 (winning bid, yen).</summary>
    public long? WinningBid { get; set; }
    /// <summary>売却基準価額 at the time of the round (yen), where the results row carries it.</summary>
    public long? SaleStandardAmount { get; set; }
    /// <summary>Number of bids (入札件数).</summary>
    public int? BidCount { get; set; }
    /// <summary>Outcome: sold / 不売 / 取下げ / 特別売却.</summary>
    public string? Outcome { get; set; }
    /// <summary>When this result row was captured.</summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// Deterministic identity for a result row: court + case label + 物件番号, so re-crawling a court's
    /// results upserts rather than duplicating. (BIT's full-history results list keys on case+item, not a
    /// sale-unit id or 開札 date.)
    /// </summary>
    public static string MakeId(string courtId, string? caseLabel, string? itemNo) =>
        $"{courtId}:{caseLabel ?? "?"}:{itemNo ?? "0"}";
}

/// <summary>
/// Per-day rollup of archive activity, keyed by JST date (<c>yyyy-MM-dd</c>). Updated by the archiver
/// on the single sequential ingestion queue (so increments never race), and read by the nightly monitor
/// to compute the PDF-archive failure rate. Also powers the Phase 3 ops dashboard.
/// </summary>
public sealed class DailyStats
{
    /// <summary>JST date <c>yyyy-MM-dd</c> — the Marten identity.</summary>
    public required string Id { get; set; }
    /// <summary>Archive attempts made (available + downloaded, whether or not they succeeded).</summary>
    public int ArchiveAttempts { get; set; }
    /// <summary>PDFs successfully archived (new content stored).</summary>
    public int PdfsArchived { get; set; }
    /// <summary>Archive attempts that failed (download error, not-a-PDF, too small).</summary>
    public int ArchiveFailures { get; set; }
    /// <summary>3点セット found already deleted / unavailable at archive time.</summary>
    public int ThreeSetUnavailable { get; set; }
    /// <summary>Mid-window re-checks performed.</summary>
    public int RechecksPerformed { get; set; }
    /// <summary>Re-checks that captured an amended (new-hash) version.</summary>
    public int AmendmentsCaptured { get; set; }
    /// <summary>Sale-result rows upserted (sweep + backfill).</summary>
    public int SaleResultsUpserted { get; set; }
}

/// <summary>Raw pre-parse capture of a BIT response, keyed by url+timestamp.</summary>
public sealed class RawCapture
{
    /// <summary>Marten identity (guid).</summary>
    public Guid Id { get; set; }
    /// <summary>Requested URL.</summary>
    public required string Url { get; set; }
    /// <summary>When captured.</summary>
    public DateTimeOffset FetchedAt { get; set; }
    /// <summary>sha256 hex of the raw bytes.</summary>
    public required string ContentHash { get; set; }
    /// <summary>Blob path of the raw bytes.</summary>
    public required string BlobPath { get; set; }
    /// <summary>HTTP status observed.</summary>
    public int StatusCode { get; set; }
    /// <summary>Content-Type observed.</summary>
    public string? ContentType { get; set; }
}
