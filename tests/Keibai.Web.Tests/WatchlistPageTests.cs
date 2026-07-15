using Bunit;
using Keibai.Core.Domain;
using Keibai.Core.Search;
using Keibai.Web.Components.Pages;
using Keibai.Web.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keibai.Web.Tests;

public sealed class WatchlistPageTests
{
    private static PropertyItem Property(
        string court = "31111",
        string unit = "31111000123",
        string? caseRaw = "令和07年(ケ)第476号",
        SaleCls? type = SaleCls.Mansion,
        string pref = "13",
        string? detailAddress = "東京都新宿区西新宿1-1-1") => new()
    {
        Id = $"{court}:{unit}",
        SaleUnitId = unit,
        CourtId = court,
        PrefectureId = pref,
        Case = caseRaw is null ? null : new CaseNumber("令和", 7, CaseType.Ke, 476, caseRaw),
        SaleCls = type,
        DetailAddress = detailAddress,
        SaleStandardAmount = 12_345_000,
        BiddingEnd = new DateOnly(2026, 8, 1),
        OpeningDate = new DateOnly(2026, 8, 8),
    };

    private static SavedSearch Search(
        string id = "s1",
        string name = "Tokyo condos under 20M",
        PropertyQuery? query = null,
        DateTimeOffset? lastRun = null) => new()
    {
        Id = id,
        Name = name,
        Query = query ?? new PropertyQuery { PrefectureId = "13", Type = SaleCls.Mansion, MaxPrice = 20_000_000 },
        CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        LastRunAt = lastRun,
    };

    private static IRenderedComponent<Watchlist> Render(FakeWatchlist fake)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<IWatchlist>(fake);
        ctx.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        return ctx.Render<Watchlist>(p => p.AddCascadingValue<HttpContext>(TestHttp.Get()));
    }

    [Fact]
    public void WatchedProperty_RendersCaseLinkAndUnstarForm()
    {
        var fake = new FakeWatchlist { WatchedProperties = [Property()] };

        var cut = Render(fake);

        // Case number is shown and linked to the property detail page.
        var link = cut.Find("a[href='/jp/property/31111/31111000123']");
        Assert.Contains("令和07年(ケ)第476号", link.TextContent);

        // An unstar form POSTs to /jp/watch carrying the property id + return path.
        var form = cut.Find("form[action='/jp/watch']");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("31111:31111000123", cut.Find("form[action='/jp/watch'] input[name='id']").GetAttribute("value"));
        Assert.Equal("/jp/watchlist", cut.Find("form[action='/jp/watch'] input[name='return']").GetAttribute("value"));
        Assert.Contains("Remove", form.TextContent);
    }

    [Fact]
    public void SavedSearch_RendersNameRunLinkAndDeleteForm()
    {
        var query = new PropertyQuery { PrefectureId = "13", Type = SaleCls.Mansion, MaxPrice = 20_000_000 };
        var fake = new FakeWatchlist { Saved = [Search(name: "My saved search", query: query)] };

        var cut = Render(fake);

        Assert.Contains("My saved search", cut.Markup);

        // Run link points at /jp with the query string built from the saved query.
        var expectedHref = "/jp" + SearchQueryString.ToQueryString(query);
        var run = cut.Find($"a[href='{expectedHref}']");
        Assert.Contains("Run", run.TextContent);
        Assert.Contains("pref=13", expectedHref); // sanity: the query really produced params

        // Delete form POSTs to /jp/search/delete with the saved-search id + return path.
        var del = cut.Find("form[action='/jp/search/delete']");
        Assert.Equal("post", del.GetAttribute("method"));
        Assert.Equal("s1", cut.Find("form[action='/jp/search/delete'] input[name='id']").GetAttribute("value"));
        Assert.Equal("/jp/watchlist", cut.Find("form[action='/jp/search/delete'] input[name='return']").GetAttribute("value"));
    }

    [Fact]
    public void EmptyLists_RenderBothEmptyStates()
    {
        var fake = new FakeWatchlist { WatchedProperties = [], Saved = [] };

        var cut = Render(fake);

        Assert.Contains("No watched properties yet", cut.Markup);
        Assert.Contains("No saved searches", cut.Markup);
        // The digest note is present regardless of content.
        Assert.Contains("nightly 08:00 JST digest", cut.Markup);
    }
}
