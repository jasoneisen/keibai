using Keibai.Core.Domain;
using Keibai.Core.Search;

namespace Keibai.Web.Reading;

/// <summary>
/// Read-side accessor for the property search + detail screens. Blazor components inject this instead of
/// touching Marten directly (mirrors offmarket.deals' <c>IDealReader</c> seam), so the pages depend only on
/// types in this assembly and the merge into offmarket.deals stays a copy-the-projects operation.
/// </summary>
public interface IPropertyReader
{
    /// <summary>One page of properties matching <paramref name="query"/> (server-side filtered/sorted/paged).</summary>
    Task<PagedResult<PropertyItem>> SearchAsync(PropertyQuery query, CancellationToken ct = default);

    /// <summary>
    /// The detail composite for <c>{courtId}:{saleUnitId}</c> (property + court + archived documents +
    /// the case's other 物件 + sale result), or <c>null</c> when no such property exists (rendered as 404).
    /// </summary>
    Task<PropertyDetailView?> GetDetailAsync(string courtId, string saleUnitId, CancellationToken ct = default);

    /// <summary>The dropdown facets for the search form (prefectures with data, courts).</summary>
    Task<SearchFacets> GetFacetsAsync(CancellationToken ct = default);

    /// <summary>
    /// The archived PDF for <paramref name="propertyItemId"/> + <paramref name="sha256"/>, streamed from the
    /// blob store (never hotlinked to BIT), or <c>null</c> when the property/document/blob is absent.
    /// </summary>
    Task<ArchivedPdf?> GetPdfAsync(string propertyItemId, string sha256, CancellationToken ct = default);

    /// <summary>
    /// ALL properties matching <paramref name="query"/> (same filters as <see cref="SearchAsync"/>, but
    /// unpaged and unsorted-by-<see cref="PropertyQuery.Sort"/>) reduced to slim map pins. Only items with
    /// usable coordinates are returned; the counts distinguish those from matches lacking coords. The pin
    /// list is capped to keep the payload bounded — 地図表示用のピン (query→pins).
    /// </summary>
    Task<MapPinsResult> GetMapPinsAsync(PropertyQuery query, CancellationToken ct = default);
}

/// <summary>
/// A slim map pin for one property (物件) — just what the map view plots and shows in a marker popup, so
/// the <c>/jp/map-pins</c> payload stays small even when returning the whole (unpaged) result set.
/// </summary>
/// <param name="CourtId">BIT court code (with <paramref name="SaleUnitId"/> keys the detail page).</param>
/// <param name="SaleUnitId">BIT sale-unit id.</param>
/// <param name="Lat">BIT-supplied latitude (validated in-bounds before the pin is emitted).</param>
/// <param name="Lng">BIT-supplied longitude (validated in-bounds before the pin is emitted).</param>
/// <param name="TypeLabel">Bilingual property-type label (土地 / 戸建 …).</param>
/// <param name="Address">Detail 所在地 if present, else the listing address.</param>
/// <param name="Price">売却基準価額 (yen), where known.</param>
/// <param name="MinBid">買受可能価額 (yen), where known.</param>
/// <param name="BiddingEnd">入札期間 end (締切) — the marker's urgency key.</param>
/// <param name="StatusLabel">Bilingual bidding-lifecycle label (入札中 / 開札 …).</param>
public sealed record MapPin(
    string CourtId,
    string SaleUnitId,
    double Lat,
    double Lng,
    string TypeLabel,
    string? Address,
    long? Price,
    long? MinBid,
    DateOnly? BiddingEnd,
    string StatusLabel);

/// <summary>The map-pins response: the plotted pins plus the counts the map view surfaces.</summary>
/// <param name="Pins">The (capped) pins, one per matching property with usable coordinates.</param>
/// <param name="Total">Count of query matches that have usable coords (may exceed <c>Pins.Count</c> when capped).</param>
/// <param name="WithoutCoords">Count of query matches lacking usable coords (null/out-of-bounds), excluded from the pins.</param>
/// <param name="Capped">True when the pin list was truncated to the cap (so the map can flag a partial view).</param>
public sealed record MapPinsResult(
    IReadOnlyList<MapPin> Pins,
    long Total,
    long WithoutCoords,
    bool Capped);

/// <summary>The property-detail composite the detail page renders.</summary>
/// <param name="Item">The property.</param>
/// <param name="Court">Its court (name), if known.</param>
/// <param name="PrefectureName">Japanese prefecture name.</param>
/// <param name="Status">Derived bidding-lifecycle label.</param>
/// <param name="Documents">Archived 3点セット documents, newest version first.</param>
/// <param name="CaseSiblings">Other properties in the same case (excludes this one).</param>
/// <param name="Result">The sale result, once published/linked.</param>
public sealed record PropertyDetailView(
    PropertyItem Item,
    Court? Court,
    string PrefectureName,
    string Status,
    IReadOnlyList<ArchivedDocument> Documents,
    IReadOnlyList<PropertyItem> CaseSiblings,
    SaleResult? Result);

/// <summary>Dropdown facets for the search filter form.</summary>
public sealed record SearchFacets(
    IReadOnlyList<PrefectureOption> Prefectures,
    IReadOnlyList<CourtOption> Courts);

/// <summary>A prefecture choice (BIT code + Japanese name).</summary>
public sealed record PrefectureOption(string Id, string Name);

/// <summary>A court choice (code + name + owning prefecture, for optional client-side grouping).</summary>
public sealed record CourtOption(string Id, string Name, string PrefectureId);

/// <summary>An archived PDF's bytes + suggested filename, for streaming to the browser.</summary>
public sealed record ArchivedPdf(byte[] Bytes, string FileName, string ContentType = "application/pdf");
