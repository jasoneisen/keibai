using Keibai.Core.Domain;

namespace Keibai.Core.Search;

/// <summary>
/// Bidding-lifecycle filter for a property search, derived from the item's schedule dates relative to
/// today (JST). Mirrors <see cref="Ingestion.RoundStatus"/>'s labels.
/// </summary>
public enum BiddingStatus
{
    /// <summary>No status filter.</summary>
    Any = 0,
    /// <summary>Before 閲覧開始 — announced but not yet viewable.</summary>
    Upcoming = 1,
    /// <summary>閲覧中 — documents viewable, bidding not yet open.</summary>
    Viewing = 2,
    /// <summary>入札中 — the bidding window is open.</summary>
    Bidding = 3,
    /// <summary>Bidding closed, 開札 not yet reached.</summary>
    Closed = 4,
    /// <summary>開札 reached or past.</summary>
    Opened = 5,
}

/// <summary>Result ordering for a property search.</summary>
public enum PropertySort
{
    /// <summary>Soonest 入札期間 end first (closest to deletion — the default operator concern).</summary>
    DeadlineAsc = 0,
    /// <summary>Soonest 開札 first.</summary>
    OpeningAsc = 1,
    /// <summary>Cheapest 売却基準価額 first.</summary>
    PriceAsc = 2,
    /// <summary>Most expensive 売却基準価額 first.</summary>
    PriceDesc = 3,
    /// <summary>Most recently first-seen first.</summary>
    NewestFirst = 4,
}

/// <summary>
/// The property-search filter. A plain, serializable record so it can be persisted inside a
/// <see cref="SavedSearch"/> and replayed by the nightly digest, AND executed by the Web reader — both go
/// through <see cref="PropertySearch.Apply"/>, so a saved search always matches exactly what the operator
/// saw on screen.
/// </summary>
public sealed record PropertyQuery
{
    /// <summary>BIT prefecture search code (02–47, 91–94).</summary>
    public string? PrefectureId { get; init; }
    /// <summary>BIT court code.</summary>
    public string? CourtId { get; init; }
    /// <summary>Property type (土地 / 戸建 / マンション / その他).</summary>
    public SaleCls? Type { get; init; }
    /// <summary>Minimum 売却基準価額 (yen, inclusive).</summary>
    public long? MinPrice { get; init; }
    /// <summary>Maximum 売却基準価額 (yen, inclusive).</summary>
    public long? MaxPrice { get; init; }
    /// <summary>Bidding-lifecycle filter.</summary>
    public BiddingStatus Status { get; init; } = BiddingStatus.Any;
    /// <summary>開札 on/after this date.</summary>
    public DateOnly? OpeningFrom { get; init; }
    /// <summary>開札 on/before this date.</summary>
    public DateOnly? OpeningTo { get; init; }
    /// <summary>Free-text substring over the (listing / detail) address.</summary>
    public string? Text { get; init; }
    /// <summary>Result ordering.</summary>
    public PropertySort Sort { get; init; } = PropertySort.DeadlineAsc;
    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;
    /// <summary>Page size.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary>True when no filter is set (an empty search = browse everything).</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(PrefectureId) && string.IsNullOrWhiteSpace(CourtId) && Type is null
        && MinPrice is null && MaxPrice is null && Status == BiddingStatus.Any
        && OpeningFrom is null && OpeningTo is null && string.IsNullOrWhiteSpace(Text);
}

/// <summary>One page of results plus the total count, for server-side paging.</summary>
/// <param name="Items">This page's rows.</param>
/// <param name="Total">Total matching rows across all pages.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)
{
    /// <summary>Total number of pages (at least 1).</summary>
    public int TotalPages => (int)Math.Max(1, Math.Ceiling(Total / (double)Math.Max(1, PageSize)));
    /// <summary>True when a previous page exists.</summary>
    public bool HasPrevious => Page > 1;
    /// <summary>True when a next page exists.</summary>
    public bool HasNext => Page < TotalPages;

    /// <summary>An empty page (for the pre-render pass / no results).</summary>
    public static PagedResult<T> Empty(int page, int pageSize) => new([], 0, page, pageSize);
}
