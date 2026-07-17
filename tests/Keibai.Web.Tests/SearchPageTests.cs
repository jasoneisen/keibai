using System.Text.RegularExpressions;
using Keibai.Core.Domain;
using Keibai.Core.Search;
using Keibai.Web.Components.Pages;
using Keibai.Web.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keibai.Web.Tests;

/// <summary>
/// bUnit component tests for the <c>/jp</c> search page. Static SSR: the page reads its filters from the
/// cascading <see cref="HttpContext"/> and loads through the injected fakes — no database needed.
/// </summary>
public sealed class SearchPageTests
{
    private static PropertyItem Item(
        string id = "31111:00000000001",
        string court = "31111",
        string unit = "00000000001",
        string pref = "14",
        SaleCls? type = SaleCls.Mansion,
        string? caseRaw = "令和07年(ケ)第476号",
        string? address = "神奈川県横浜市中区1-2-3",
        long? standard = 12_345_000,
        long? minimum = 9_876_000) => new()
        {
            Id = id,
            SaleUnitId = unit,
            CourtId = court,
            PrefectureId = pref,
            SaleCls = type,
            Case = caseRaw is null ? null : new CaseNumber("令和", 7, CaseType.Ke, 476, caseRaw),
            RawAddress = address,
            DetailAddress = address,
            SaleStandardAmount = standard,
            MinimumBidAmount = minimum,
            BiddingStart = new DateOnly(2026, 8, 1),
            BiddingEnd = new DateOnly(2026, 8, 8),
            OpeningDate = new DateOnly(2026, 8, 15),
        };

    private static Bunit.BunitContext NewContext(FakePropertyReader reader, IWatchlist? watchlist = null)
    {
        var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton<IPropertyReader>(reader);
        ctx.Services.AddSingleton(watchlist ?? new FakeWatchlist());
        ctx.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        return ctx;
    }

    [Fact]
    public void Renders_row_per_result_with_detail_link_and_yen_and_captures_parsed_query()
    {
        var reader = new FakePropertyReader
        {
            SearchResult = new PagedResult<PropertyItem>([Item()], 1, 1, 25),
        };
        using var ctx = NewContext(reader);

        var cut = ctx.Render<Search>(p =>
            p.AddCascadingValue<HttpContext>(TestHttp.Get("?pref=14&type=Mansion")));

        // One data row (star + case link) with the formatted yen and the detail-page link.
        Assert.Contains("/jp/property/31111/00000000001", cut.Markup);
        Assert.Contains("令和07年(ケ)第476号", cut.Markup);
        Assert.Contains("¥12,345,000", cut.Markup);

        // The parsed filters flowed into the reader.
        Assert.NotNull(reader.LastQuery);
        Assert.Equal("14", reader.LastQuery!.PrefectureId);
        Assert.Equal(SaleCls.Mansion, reader.LastQuery.Type);
    }

    [Fact]
    public void Shows_empty_state_when_no_results()
    {
        var reader = new FakePropertyReader
        {
            SearchResult = PagedResult<PropertyItem>.Empty(1, 25),
        };
        using var ctx = NewContext(reader);

        var cut = ctx.Render<Search>(p =>
            p.AddCascadingValue<HttpContext>(TestHttp.Get()));

        Assert.Contains("alert alert-info", cut.Markup);
        Assert.DoesNotContain("/jp/property/", cut.Markup);
    }

    [Fact]
    public void Renders_pager_when_results_span_multiple_pages()
    {
        var items = new[] { Item(), Item(id: "31111:00000000002", unit: "00000000002") };
        var reader = new FakePropertyReader
        {
            SearchResult = new PagedResult<PropertyItem>(items, 100, 2, 25),
        };
        using var ctx = NewContext(reader);

        var cut = ctx.Render<Search>(p =>
            p.AddCascadingValue<HttpContext>(TestHttp.Get("?page=2")));

        Assert.Contains("pagination", cut.Markup);
        Assert.Contains("data-enhance-nav=\"false\"", cut.Markup);
        Assert.Contains("?page=", cut.Markup);
        Assert.Contains("Page 2 of 4", cut.Markup);
    }

    [Fact]
    public void Pdf_badge_marks_archived_rows_and_docs_filter_is_parsed()
    {
        var archived = Item(unit: "00000000001");
        archived.LastArchivedAt = DateTimeOffset.UtcNow;
        var plain = Item(id: "31111:00000000002", unit: "00000000002", caseRaw: "令和07年(ケ)第477号");
        var reader = new FakePropertyReader
        {
            SearchResult = new PagedResult<PropertyItem>([archived, plain], 2, 1, 25),
        };
        using var ctx = NewContext(reader);

        var cut = ctx.Render<Search>(p =>
            p.AddCascadingValue<HttpContext>(TestHttp.Get("?docs=1")));

        // The PDF badge marks exactly the archived row (its title text is unique to the badge).
        Assert.Single(Regex.Matches(cut.Markup, @"Archived 3点セット \(PDF\) available"));
        // The "has documents" filter parsed into the query, and the checkbox is present.
        Assert.True(reader.LastQuery!.HasDocuments);
        Assert.Contains("Only with archived documents", cut.Markup);
    }

    [Fact]
    public void Status_checkbox_group_defaults_to_everything_except_Closed()
    {
        var reader = new FakePropertyReader
        {
            SearchResult = new PagedResult<PropertyItem>([Item()], 1, 1, 25),
        };
        using var ctx = NewContext(reader);

        // No status params ⇒ the all-but-Closed default flows into the query and the checkboxes.
        var cut = ctx.Render<Search>(p => p.AddCascadingValue<HttpContext>(TestHttp.Get()));

        Assert.Equal(
            [BiddingStatus.Upcoming, BiddingStatus.Viewing, BiddingStatus.Bidding, BiddingStatus.Opened],
            reader.LastQuery!.Statuses);

        // The hidden sentinel is always submitted; a checkbox exists for every lifecycle status.
        Assert.Contains("name=\"statusSet\"", cut.Markup);
        foreach (var s in Enum.GetValues<BiddingStatus>())
        {
            Assert.Contains($"id=\"status-{s}\"", cut.Markup);
        }

        // Closed is the only box left unchecked by default (a checked box renders the bare `checked` attr).
        Assert.Matches(@"id=""status-Bidding""[^>]*\schecked", cut.Markup);
        Assert.DoesNotMatch(@"id=""status-Closed""[^>]*\schecked", cut.Markup);
    }

    [Fact]
    public void Multi_status_url_flows_into_the_query()
    {
        var reader = new FakePropertyReader
        {
            SearchResult = new PagedResult<PropertyItem>([Item()], 1, 1, 25),
        };
        using var ctx = NewContext(reader);

        var cut = ctx.Render<Search>(p =>
            p.AddCascadingValue<HttpContext>(TestHttp.Get("?status=Bidding&status=Closed")));

        Assert.Equal([BiddingStatus.Bidding, BiddingStatus.Closed], reader.LastQuery!.Statuses);
        Assert.Matches(@"id=""status-Closed""[^>]*\schecked", cut.Markup);
        Assert.DoesNotMatch(@"id=""status-Viewing""[^>]*\schecked", cut.Markup);
    }
}
