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
}

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
