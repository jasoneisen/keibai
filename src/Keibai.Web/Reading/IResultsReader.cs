using Keibai.Core.Domain;
using Keibai.Core.Search;

namespace Keibai.Web.Reading;

/// <summary>Read-side accessor for the past-results explorer (<c>/jp/results</c>).</summary>
public interface IResultsReader
{
    /// <summary>One page of 売却結果 matching <paramref name="query"/>, newest 開札 first.</summary>
    Task<PagedResult<SaleResultView>> SearchAsync(ResultsQuery query, CancellationToken ct = default);

    /// <summary>Per-prefecture summary stats (sale rate + median winning-bid ratio).</summary>
    Task<IReadOnlyList<PrefectureResultStats>> PrefectureStatsAsync(CancellationToken ct = default);

    /// <summary>Dropdown facets (prefectures + courts that have results, distinct outcomes).</summary>
    Task<ResultsFacets> GetFacetsAsync(CancellationToken ct = default);
}

/// <summary>Filter for the past-results explorer.</summary>
public sealed record ResultsQuery
{
    /// <summary>BIT prefecture code (resolved to that prefecture's court ids).</summary>
    public string? PrefectureId { get; init; }
    /// <summary>BIT court code.</summary>
    public string? CourtId { get; init; }
    /// <summary>Outcome filter (売却 / 不売 / 取下げ / 特別売却).</summary>
    public string? Outcome { get; init; }
    /// <summary>1-based page.</summary>
    public int Page { get; init; } = 1;
    /// <summary>Rows per page.</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>A sale-result row with the joined court/prefecture name and the computed bid ratio.</summary>
/// <param name="Result">The stored result.</param>
/// <param name="CourtName">Court name, if known.</param>
/// <param name="PrefectureName">Japanese prefecture name (via the court), or "?".</param>
/// <param name="BidRatio">売却価額 / 売却基準価額, when both are present.</param>
/// <param name="PropertyTracked">True when this result is linked to a still-tracked property (detail link).</param>
public sealed record SaleResultView(
    SaleResult Result,
    string? CourtName,
    string PrefectureName,
    double? BidRatio,
    bool PropertyTracked);

/// <summary>Per-prefecture roll-up for the results explorer's summary band.</summary>
/// <param name="PrefectureId">BIT prefecture code.</param>
/// <param name="PrefectureName">Japanese name.</param>
/// <param name="Total">Total results.</param>
/// <param name="Sold">売却 + 特別売却 count.</param>
/// <param name="SaleRate">Sold / Total.</param>
/// <param name="MedianBidRatio">Median 売却価額/売却基準価額 across sold rows that carry both.</param>
public sealed record PrefectureResultStats(
    string PrefectureId,
    string PrefectureName,
    int Total,
    int Sold,
    double SaleRate,
    double? MedianBidRatio);

/// <summary>Dropdown facets for the results filter form.</summary>
public sealed record ResultsFacets(
    IReadOnlyList<PrefectureOption> Prefectures,
    IReadOnlyList<CourtOption> Courts,
    IReadOnlyList<string> Outcomes);
