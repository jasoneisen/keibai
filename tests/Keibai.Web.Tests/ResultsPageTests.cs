using Bunit;
using Keibai.Core.Domain;
using Keibai.Core.Search;
using Keibai.Web.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ResultsPage = Keibai.Web.Components.Pages.Results;

namespace Keibai.Web.Tests;

/// <summary>Static-SSR component tests for the 売却結果 explorer (<c>/jp/results</c>).</summary>
public sealed class ResultsPageTests
{
    private static SaleResultView View(
        string caseLabel,
        long? standard = null,
        long? winning = null,
        string? outcome = null,
        double? bidRatio = null,
        string? propertyItemId = null,
        bool tracked = false,
        string courtId = "31111",
        string? courtName = "東京地方裁判所",
        string prefectureName = "東京都") =>
        new(
            new SaleResult
            {
                Id = SaleResult.MakeId(courtId, caseLabel, "1"),
                PropertyItemId = propertyItemId,
                CourtId = courtId,
                ItemNo = "1",
                CaseLabel = caseLabel,
                OpeningDate = new DateOnly(2026, 6, 1),
                WinningBid = winning,
                SaleStandardAmount = standard,
                BidCount = 3,
                Outcome = outcome,
            },
            courtName,
            prefectureName,
            bidRatio,
            tracked);

    private static PrefectureResultStats Stat(
        string id = "13",
        string name = "東京都",
        int total = 10,
        int sold = 7,
        double saleRate = 0.7,
        double? medianRatio = 1.15) =>
        new(id, name, total, sold, saleRate, medianRatio);

    [Fact]
    public void Renders_result_row_and_prefecture_stats_band_and_captures_prefecture_filter()
    {
        var reader = new FakeResultsReader
        {
            SearchResult = new PagedResult<SaleResultView>(
                [View("令和07年(ケ)第476号", standard: 12_000_000, winning: 15_600_000, outcome: "売却")],
                Total: 1,
                Page: 1,
                PageSize: 50),
            Stats = [Stat()],
        };

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IResultsReader>(reader);
        var cut = ctx.Render<ResultsPage>(p => p.AddCascadingValue<HttpContext>(TestHttp.Get("?pref=13")));
        var html = cut.Markup;

        Assert.Contains("令和07年(ケ)第476号", html);
        Assert.Contains("¥15,600,000", html);
        Assert.Contains("売却", html);
        // Per-prefecture stats band.
        Assert.Contains("Sale rate", html);
        Assert.Contains("東京都", html);

        Assert.NotNull(reader.LastQuery);
        Assert.Equal("13", reader.LastQuery!.PrefectureId);
    }

    [Fact]
    public void Tracked_result_links_case_label_to_property_detail()
    {
        var reader = new FakeResultsReader
        {
            SearchResult = new PagedResult<SaleResultView>(
                [View("令和08年(ヌ)第12号", outcome: "売却", propertyItemId: "31111:00000079036", tracked: true)],
                Total: 1,
                Page: 1,
                PageSize: 50),
        };

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IResultsReader>(reader);
        var cut = ctx.Render<ResultsPage>(p => p.AddCascadingValue<HttpContext>(TestHttp.Get()));

        var link = cut.Find("a[href='/jp/property/31111/00000079036']");
        Assert.Contains("令和08年(ヌ)第12号", link.TextContent);
    }

    [Fact]
    public void Empty_result_set_renders_info_alert()
    {
        var reader = new FakeResultsReader
        {
            SearchResult = PagedResult<SaleResultView>.Empty(1, 50),
        };

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IResultsReader>(reader);
        var cut = ctx.Render<ResultsPage>(p => p.AddCascadingValue<HttpContext>(TestHttp.Get()));

        var alert = cut.Find("div.alert.alert-info");
        Assert.Contains("No sale results", alert.TextContent);
    }
}
