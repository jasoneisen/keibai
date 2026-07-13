using Keibai.Core.Domain;
using Keibai.Core.Parsing;
using Xunit;

namespace Keibai.Tests;

public class ListingParserTests
{
    [Fact]
    public void Parses_pagination_envelope_from_tokyo_fixture()
    {
        var page = ListingParser.Parse(Fixtures.TokyoResults());

        Assert.Equal(42, page.TotalCount);
        Assert.Equal(1, page.CurrentPage);
        Assert.True(page.Rows.Count > 0);
    }

    [Fact]
    public void Parses_the_known_tachikawa_row()
    {
        var page = ListingParser.Parse(Fixtures.TokyoResults());

        var row = page.Rows.Single(r => r.SaleUnitId == "00000021309");
        Assert.Equal("31131", row.CourtId);
        Assert.Contains("立川支部", row.CourtName);
        Assert.NotNull(row.Case);
        Assert.Equal(CaseType.Nu, row.Case!.Type);
        Assert.Equal(SaleCls.Land, row.SaleCls);
        Assert.Equal(5_750_000, row.SaleStandardAmount);
        Assert.Contains("青梅市", row.RawAddress);
    }

    [Fact]
    public void Rows_are_deduped_by_court_and_sale_unit()
    {
        var page = ListingParser.Parse(Fixtures.TokyoResults());

        var keys = page.Rows.Select(r => $"{r.CourtId}:{r.SaleUnitId}").ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
