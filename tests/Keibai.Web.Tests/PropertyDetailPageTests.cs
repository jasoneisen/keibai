using Bunit;
using Keibai.Core.Domain;
using Keibai.Web.Components.Pages;
using Keibai.Web.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keibai.Web.Tests;

/// <summary>
/// Static-SSR component tests for <see cref="PropertyDetail"/>. All reads go through
/// <see cref="FakePropertyReader"/> / <see cref="FakeWatchlist"/>, so no DB or host is involved.
/// </summary>
public sealed class PropertyDetailPageTests
{
    private const string CourtId = "31111";
    private const string SaleUnitId = "00000079036";
    private const string Sha = "abc123def456";

    [Fact]
    public void Renders_header_amounts_document_link_and_captures_lookup_key()
    {
        var reader = new FakePropertyReader { Detail = SampleDetail() };

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IPropertyReader>(reader);
        ctx.Services.AddSingleton<IWatchlist>(new FakeWatchlist());

        var cut = ctx.Render<PropertyDetail>(p => p
            .Add(x => x.CourtId, CourtId).Add(x => x.SaleUnitId, SaleUnitId)
            .AddCascadingValue<HttpContext>(TestHttp.Get()));

        var markup = cut.Markup;

        // Case number in the header.
        Assert.Contains("令和07年(ケ)第476号", markup);
        // Formatted yen (売却基準価額 = 12,345,000).
        Assert.Contains("¥12,345,000", markup);
        // Content-addressed document link.
        Assert.Contains($"/jp/doc/{CourtId}/{SaleUnitId}/{Sha}", markup);

        // The reader was queried with the exact route params.
        Assert.Equal((CourtId, SaleUnitId), reader.LastDetail);
    }

    [Fact]
    public void Missing_property_renders_not_found_and_sets_404()
    {
        var reader = new FakePropertyReader { Detail = null };
        var http = TestHttp.Get();

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IPropertyReader>(reader);
        ctx.Services.AddSingleton<IWatchlist>(new FakeWatchlist());

        var cut = ctx.Render<PropertyDetail>(p => p
            .Add(x => x.CourtId, CourtId).Add(x => x.SaleUnitId, SaleUnitId)
            .AddCascadingValue<HttpContext>(http));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusCodes.Status404NotFound, http.Response.StatusCode);
    }

    [Fact]
    public void Renders_per_item_attribute_table_and_sale_result_block()
    {
        var reader = new FakePropertyReader { Detail = SampleDetail() };

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IPropertyReader>(reader);
        ctx.Services.AddSingleton<IWatchlist>(new FakeWatchlist());

        var cut = ctx.Render<PropertyDetail>(p => p
            .Add(x => x.CourtId, CourtId).Add(x => x.SaleUnitId, SaleUnitId)
            .AddCascadingValue<HttpContext>(TestHttp.Get()));

        var markup = cut.Markup;

        // Per-物件 detail: the raw attribute key and value are both surfaced.
        Assert.Contains("所在", markup);
        Assert.Contains("東京都新宿区西新宿一丁目", markup);

        // Sale-result block: outcome + winning bid.
        Assert.Contains("売却結果", markup);
        Assert.Contains("¥15,000,000", markup);
        Assert.Contains("sold", markup);
    }

    private static PropertyDetailView SampleDetail()
    {
        var item = new PropertyItem
        {
            Id = $"{CourtId}:{SaleUnitId}",
            SaleUnitId = SaleUnitId,
            CourtId = CourtId,
            PrefectureId = "13",
            SaleCls = SaleCls.Mansion,
            Case = new CaseNumber("令和", 7, CaseType.Ke, 476, "令和07年(ケ)第476号"),
            RawAddress = "東京都新宿区西新宿一丁目",
            DetailAddress = "東京都新宿区西新宿一丁目1番1",
            SaleStandardAmount = 12_345_000,
            MinimumBidAmount = 9_876_000,
            Structure = "鉄筋コンクリート造",
            RoomLayout = "3LDK",
            ItemCount = 1,
            Items =
            [
                new PropertyItemDetail
                {
                    ItemNo = "1",
                    Kind = "区分所有建物",
                    Attributes = new Dictionary<string, string>
                    {
                        ["所在"] = "東京都新宿区西新宿一丁目",
                        ["家屋番号"] = "1番1の101",
                    },
                },
            ],
        };

        var doc = new ArchivedDocument
        {
            Id = $"{item.Id}:{Sha}",
            PropertyItemId = item.Id,
            Sha256 = Sha,
            Kind = "combined",
            Version = 2,
            ByteSize = 2_500_000,
            SourceUrl = "https://www.bit.courts.go.jp/example.pdf",
            FetchedAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
            BlobPath = "blobs/ab/abc123def456",
        };

        var result = new SaleResult
        {
            Id = SaleResult.MakeId(CourtId, "令和07年(ケ)第476号", "1"),
            PropertyItemId = item.Id,
            CourtId = CourtId,
            ItemNo = "1",
            CaseLabel = "令和07年(ケ)第476号",
            WinningBid = 15_000_000,
            SaleStandardAmount = 12_345_000,
            BidCount = 4,
            Outcome = "sold",
        };

        return new PropertyDetailView(
            item,
            new Court { Id = CourtId, Name = "東京地方裁判所本庁", PrefectureId = "13" },
            "東京都",
            "bidding",
            [doc],
            [],
            result);
    }
}
