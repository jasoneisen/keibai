using Keibai.Core.Domain;
using Keibai.Core.Parsing;

namespace Keibai.Core.Ingestion;

/// <summary>
/// Maps a parsed <see cref="PropertyDetail"/> onto a <see cref="PropertyItem"/>. Shared by the live
/// detail sweep (<c>SyncPropertyDetail</c>) and the offline replay-from-capture backfill
/// (<c>ReparseDetailCaptures</c>) so both enrich identically.
/// </summary>
public static class DetailEnrichment
{
    /// <summary>Enrich <paramref name="item"/> from a freshly-parsed detail page.</summary>
    public static void Apply(PropertyItem item, PropertyDetail detail)
    {
        item.Latitude = detail.Latitude ?? item.Latitude;
        item.Longitude = detail.Longitude ?? item.Longitude;
        item.Case ??= detail.Case;

        // The detail header badge is the authoritative card type (土地/戸建て/マンション) — overwrite the
        // listing value, which historically mis-typed 戸建て (trailing て) cards. Keep the old value only
        // when the detail badge is absent.
        item.SaleCls = detail.SaleCls ?? item.SaleCls;
        item.ViewingStart = detail.ViewingStart ?? item.ViewingStart;
        item.BiddingStart = detail.BiddingStart ?? item.BiddingStart;
        item.BiddingEnd = detail.BiddingEnd ?? item.BiddingEnd;
        item.OpeningDate = detail.OpeningDate ?? item.OpeningDate;
        item.SaleDecisionDate = detail.SaleDecisionDate ?? item.SaleDecisionDate;
        item.SaleStandardAmount = detail.SaleStandardAmount ?? item.SaleStandardAmount;
        item.MinimumBidAmount = detail.MinimumBidAmount ?? item.MinimumBidAmount;

        // Full per-物件 attribute set + typed rollups.
        DetailParser.ApplyAttributeRollups(item, detail.Items);
    }
}
