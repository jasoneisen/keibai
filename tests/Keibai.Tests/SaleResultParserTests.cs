using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Parsing;
using Xunit;

namespace Keibai.Tests;

public class SaleResultParserTests
{
    [Fact]
    public void Parses_court_context_from_the_h02_prefecture_page()
    {
        var ctx = ResultForms.ParseCourtContext(Fixtures.SaleResultPrefPage());

        Assert.Equal("000805", ctx.FiscalYear);
        Assert.Equal("2", ctx.CodeCls);
        Assert.Contains("31111", ctx.CourtIds); // 東京地方裁判所本庁
        Assert.Contains("31131", ctx.CourtIds); // 立川支部
    }

    [Fact]
    public void Parses_the_results_listing_total_and_rows()
    {
        var page = SaleResultParser.Parse(Fixtures.SaleResultsListing());

        Assert.Equal(148, page.TotalCount); // the court's full retained history
        Assert.NotEmpty(page.Rows);
        // Every row carries a case label.
        Assert.All(page.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.CaseLabel)));
    }

    [Fact]
    public void Parses_a_sold_row_field_by_field()
    {
        var page = SaleResultParser.Parse(Fixtures.SaleResultsListing());
        var sold = Assert.Single(page.Rows, r => r.CaseLabel is not null && r.CaseLabel.Contains("第476号"));

        Assert.Equal(SaleCls.Land, sold.SaleCls);
        Assert.Equal("売却", sold.Outcome);
        Assert.Equal(241_500_000, sold.WinningBid);
        Assert.Equal(123_000_000, sold.SaleStandardAmount);
        Assert.Equal(19, sold.BidCount);
        Assert.Equal("1", sold.ItemNo);
        Assert.Equal(CaseType.Ke, sold.Case!.Type);
    }

    [Fact]
    public void A_not_sold_row_has_no_winning_bid()
    {
        var page = SaleResultParser.Parse(Fixtures.SaleResultsListing());
        // The fixture has 取下げ / 不売 / 特別売却 outcomes alongside 売却.
        Assert.Contains(page.Rows, r => r.Outcome is "取下げ" or "不売");
        Assert.All(page.Rows.Where(r => r.Outcome is "取下げ" or "不売"), r => Assert.Null(r.WinningBid));
    }

    [Fact]
    public void Page_two_is_a_distinct_set_of_results()
    {
        var p1 = SaleResultParser.Parse(Fixtures.SaleResultsListing());
        var p2 = SaleResultParser.Parse(Fixtures.SaleResultsPage2());

        Assert.Equal(2, p2.CurrentPage);
        var p1Cases = p1.Rows.Select(r => r.CaseLabel).ToHashSet();
        Assert.NotEmpty(p2.Rows);
        Assert.DoesNotContain(p2.Rows, r => p1Cases.Contains(r.CaseLabel));
    }
}
