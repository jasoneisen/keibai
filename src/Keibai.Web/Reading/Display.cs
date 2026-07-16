using System.Globalization;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Search;

namespace Keibai.Web.Reading;

/// <summary>
/// Small, pure presentation helpers shared by the Blazor pages so formatting stays consistent and the
/// pages stay thin. English labels, Japanese data as-is (per the Phase 3 brief).
/// </summary>
public static class Display
{
    /// <summary>Format a yen amount as <c>¥12,345,000</c> (or <c>—</c> when null).</summary>
    public static string Yen(long? amount) =>
        amount is { } v ? "¥" + v.ToString("#,0", CultureInfo.InvariantCulture) : "—";

    /// <summary>Format a date as ISO <c>yyyy-MM-dd</c> (or <c>—</c> when null).</summary>
    public static string Date(DateOnly? d) =>
        d is { } v ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    /// <summary>The Japanese prefecture name for a BIT code.</summary>
    public static string Prefecture(string? code) => PrefectureNames.Of(code);

    /// <summary>Bilingual property-type label.</summary>
    public static string TypeLabel(SaleCls? cls) => cls switch
    {
        SaleCls.Land => "土地 / Land",
        SaleCls.Detached => "戸建 / Detached",
        SaleCls.Mansion => "マンション / Condo",
        SaleCls.Other => "その他 / Other",
        _ => "—",
    };

    /// <summary>Bilingual bidding-status label for a <see cref="Core.Search.PropertySearch.DeriveStatus"/> value.</summary>
    public static string StatusLabel(string? status) => status switch
    {
        "upcoming" => "予定 / Upcoming",
        "viewing" => "閲覧中 / Viewing",
        "bidding" => "入札中 / Bidding",
        "closed" => "入札終了 / Closed",
        "opened" => "開札 / Opened",
        _ => "—",
    };

    /// <summary>Bilingual label for a bidding-status filter option.</summary>
    public static string StatusFilterLabel(BiddingStatus status) => status switch
    {
        BiddingStatus.Any => "すべて / All",
        BiddingStatus.Upcoming => "予定 / Upcoming",
        BiddingStatus.Viewing => "閲覧中 / Viewing",
        BiddingStatus.Bidding => "入札中 / Bidding",
        BiddingStatus.Closed => "入札終了 / Closed",
        BiddingStatus.Opened => "開札 / Opened",
        _ => status.ToString(),
    };

    /// <summary>Bilingual label for a result-ordering option.</summary>
    public static string SortLabel(PropertySort sort) => sort switch
    {
        PropertySort.DeadlineAsc => "締切が近い順 / Deadline",
        PropertySort.OpeningAsc => "開札が近い順 / Opening date",
        PropertySort.PriceAsc => "価格が安い順 / Price ↑",
        PropertySort.PriceDesc => "価格が高い順 / Price ↓",
        PropertySort.NewestFirst => "新着順 / Newest",
        _ => sort.ToString(),
    };

    /// <summary>Bootstrap contextual badge class for a bidding status.</summary>
    public static string StatusBadgeClass(string? status) => status switch
    {
        "bidding" => "text-bg-success",
        "viewing" => "text-bg-primary",
        "closed" => "text-bg-warning",
        "opened" => "text-bg-secondary",
        "upcoming" => "text-bg-info",
        _ => "text-bg-light",
    };

    /// <summary>
    /// A short "days until 入札 deadline" label for a table badge, e.g. <c>3d left</c> / <c>today</c> /
    /// <c>closed</c>. Uses the 入札期間 end (the archive-priority / deletion key).
    /// </summary>
    public static string Deadline(DateOnly? biddingEnd, DateOnly today)
    {
        if (biddingEnd is not { } end)
        {
            return "—";
        }

        var days = end.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => "closed",
            0 => "today",
            1 => "1d left",
            _ => $"{days}d left",
        };
    }

    /// <summary>Bootstrap badge class for a deadline urgency (red ≤2 days, amber ≤7, else muted).</summary>
    public static string DeadlineClass(DateOnly? biddingEnd, DateOnly today)
    {
        if (biddingEnd is not { } end)
        {
            return "text-bg-light";
        }

        var days = end.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => "text-bg-light",
            <= 2 => "text-bg-danger",
            <= 7 => "text-bg-warning",
            _ => "text-bg-light",
        };
    }

    /// <summary>The winning-bid : 売却基準価額 ratio as a percentage string (or <c>—</c>).</summary>
    public static string Ratio(double? ratio) =>
        ratio is { } r ? r.ToString("P0", CultureInfo.InvariantCulture) : "—";

    /// <summary>A Google Maps search URL for a raw address (geocode confidence is never trusted — flag it in UI).</summary>
    public static string GoogleMapsUrl(string? address) =>
        "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(address ?? string.Empty);
}
