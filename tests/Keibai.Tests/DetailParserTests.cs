using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Parsing;
using Xunit;

namespace Keibai.Tests;

public class DetailParserTests
{
    [Fact]
    public void Parses_latlng_and_ids_from_detail_fixture()
    {
        var detail = DetailParser.Parse(Fixtures.Detail());

        Assert.Equal("00000021309", detail.SaleUnitId);
        Assert.Equal("31131", detail.CourtId);
        Assert.True(detail.HasThreeSetPdf);
        Assert.NotNull(detail.Latitude);
        Assert.NotNull(detail.Longitude);
        Assert.InRange(detail.Latitude!.Value, 35.0, 36.0);
        Assert.InRange(detail.Longitude!.Value, 139.0, 140.0);
    }

    [Fact]
    public void Parses_bidding_schedule_from_detail_fixture()
    {
        var detail = DetailParser.Parse(Fixtures.Detail());

        Assert.Equal(new DateOnly(2026, 6, 26), detail.ViewingStart);
        Assert.Equal(new DateOnly(2026, 7, 15), detail.BiddingStart);
        Assert.Equal(new DateOnly(2026, 7, 22), detail.BiddingEnd);
        Assert.Equal(new DateOnly(2026, 7, 28), detail.OpeningDate);
        Assert.Equal(new DateOnly(2026, 8, 12), detail.SaleDecisionDate);
    }

    [Fact]
    public void Recovers_type_and_prices_from_detail_fixture()
    {
        // The multi-item-card follow-up: 種別 is recoverable from the detail page even when the listing
        // badge omitted it. The detail page also carries 買受可能価額, which the listing does not.
        var detail = DetailParser.Parse(Fixtures.Detail());

        Assert.Equal(SaleCls.Land, detail.SaleCls);
        Assert.Equal(5_750_000, detail.SaleStandardAmount);
        Assert.Equal(4_600_000, detail.MinimumBidAmount);
    }

    [Fact]
    public void Detail_form_carries_full_result_envelope()
    {
        // BIT 500s on a partial detail POST; the form must carry the full results-page envelope with
        // the driving fields overridden. Prove the extracted form is large and has the detail keys.
        var form = BitForms.PropertyDetail(Fixtures.TokyoResults(), "31131", "00000021309");

        Assert.Equal("00000021309", form["saleUnitId"]);
        Assert.Equal("31131", form["detailCourtId"]);
        Assert.True(form.Count > 50, $"expected the full envelope, got {form.Count} fields");
    }
}
