using Keibai.Core.Domain;
using Keibai.Core.Ingestion;

namespace Keibai.Core.Search;

/// <summary>
/// Translates a <see cref="PropertyQuery"/> into Marten <c>Where</c>/<c>OrderBy</c> over the
/// <see cref="PropertyItem"/> collection — the SINGLE source of truth for search semantics, shared by the
/// Web reader (interactive search) and the nightly saved-search digest. Every filtered column
/// (prefecture, court, type, price, 開札 date) is indexed in <c>AddKeibai</c>, so a typical filter never
/// full-scans the collection.
/// </summary>
public static class PropertySearch
{
    /// <summary>Apply the filter clauses of <paramref name="f"/> to <paramref name="q"/> (no ordering/paging).</summary>
    public static IQueryable<PropertyItem> Apply(IQueryable<PropertyItem> q, PropertyQuery f, DateOnly today)
    {
        if (!string.IsNullOrWhiteSpace(f.PrefectureId))
        {
            q = q.Where(x => x.PrefectureId == f.PrefectureId);
        }

        if (!string.IsNullOrWhiteSpace(f.CourtId))
        {
            q = q.Where(x => x.CourtId == f.CourtId);
        }

        if (f.Type is { } type)
        {
            q = q.Where(x => x.SaleCls == type);
        }

        if (f.MinPrice is { } min)
        {
            q = q.Where(x => x.SaleStandardAmount >= min);
        }

        if (f.MaxPrice is { } max)
        {
            q = q.Where(x => x.SaleStandardAmount <= max);
        }

        if (f.OpeningFrom is { } from)
        {
            q = q.Where(x => x.OpeningDate >= from);
        }

        if (f.OpeningTo is { } to)
        {
            q = q.Where(x => x.OpeningDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            var term = f.Text.Trim();
            // ILIKE substring match (no tokenization) — robust for 地番 / 住居表示 Japanese addresses,
            // which a default Postgres full-text config would not tokenize.
            q = q.Where(x =>
                (x.RawAddress != null && x.RawAddress.Contains(term))
                || (x.DetailAddress != null && x.DetailAddress.Contains(term)));
        }

        return ApplyStatus(q, f.Status, today);
    }

    private static IQueryable<PropertyItem> ApplyStatus(
        IQueryable<PropertyItem> q, BiddingStatus status, DateOnly today) =>
        status switch
        {
            BiddingStatus.Upcoming => q.Where(x => x.ViewingStart == null || x.ViewingStart > today),
            BiddingStatus.Viewing => q.Where(x =>
                x.ViewingStart <= today && (x.BiddingStart == null || x.BiddingStart > today)),
            BiddingStatus.Bidding => q.Where(x => x.BiddingStart <= today && x.BiddingEnd >= today),
            BiddingStatus.Closed => q.Where(x => x.BiddingEnd < today && x.OpeningDate >= today),
            BiddingStatus.Opened => q.Where(x => x.OpeningDate < today),
            _ => q,
        };

    /// <summary>Order results per <paramref name="sort"/> with a stable tie-break on Id.</summary>
    public static IQueryable<PropertyItem> OrderFor(IQueryable<PropertyItem> q, PropertySort sort) =>
        sort switch
        {
            PropertySort.OpeningAsc => q.OrderBy(x => x.OpeningDate!).ThenBy(x => x.Id),
            PropertySort.PriceAsc => q.OrderBy(x => x.SaleStandardAmount!).ThenBy(x => x.Id),
            PropertySort.PriceDesc => q.OrderByDescending(x => x.SaleStandardAmount!).ThenBy(x => x.Id),
            PropertySort.NewestFirst => q.OrderByDescending(x => x.FirstSeen).ThenBy(x => x.Id),
            _ => q.OrderBy(x => x.BiddingEnd!).ThenBy(x => x.Id),
        };

    /// <summary>
    /// The bidding-lifecycle label for a single item (for a status badge / watchlist diff), or
    /// <c>"unknown"</c> when the item has no 開札期日.
    /// </summary>
    public static string DeriveStatus(PropertyItem item, DateOnly today) =>
        item.OpeningDate is { } opening
            ? RoundStatus.Derive(item.ViewingStart, item.BiddingStart, item.BiddingEnd, opening, today)
            : "unknown";
}
