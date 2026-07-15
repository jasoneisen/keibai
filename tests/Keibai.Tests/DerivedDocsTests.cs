using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Keibai.Tests;

[Collection("host")]
public class DerivedDocsTests(HostFixture fixture)
{
    private const string CaseRaw = "令和07年(ケ)第5号";

    [Fact]
    public async Task Rebuilds_case_and_round_and_links_sale_results_to_their_property()
    {
        // Isolated ids so this test can share the "host" collection without colliding with other tests.
        var court = "T" + Guid.NewGuid().ToString("N")[..4];
        var caseNumber = new CaseNumber("令和", 7, CaseType.Ke, 5, CaseRaw);
        var item1 = MakeItem(court, "1", caseNumber);
        var item2 = MakeItem(court, "2", caseNumber);
        var result1 = MakeResult(court, "1");
        var result2 = MakeResult(court, "2");

        await using (var seed = fixture.Store.LightweightSession())
        {
            seed.Store(item1, item2);
            seed.Store(result1, result2);
            await seed.SaveChangesAsync();
        }

        // 2026-07-20 (JST) sits between ViewingStart (06-26) and BiddingStart (07-22) → "viewing".
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-20T12:00:00+09:00"));
        await RebuildHandler.Handle(
            new RebuildDerivedDocuments(),
            fixture.Host.Services.GetRequiredService<IKeibaiStoreAccessor>(),
            time,
            NullLogger<RebuildHandlerMarker>.Instance,
            CancellationToken.None);

        await using var q = fixture.Store.QuerySession();

        var auctionCase = Assert.Single(await q.Query<AuctionCase>().Where(c => c.CourtId == court).ToListAsync());
        Assert.Equal(2, auctionCase.PropertyCount);
        Assert.Equal([item1.Id, item2.Id], auctionCase.PropertyItemIds.OrderBy(x => x).ToList());

        var round = Assert.Single(await q.Query<AuctionRound>().Where(r => r.CourtId == court).ToListAsync());
        Assert.EndsWith(":2026-07-28", round.Id);
        Assert.Equal(2, round.PropertyCount);
        Assert.Equal("viewing", round.Status);

        var linked1 = await q.LoadAsync<SaleResult>(result1.Id);
        var linked2 = await q.LoadAsync<SaleResult>(result2.Id);
        Assert.Equal(item1.Id, linked1!.PropertyItemId);
        Assert.Equal(item2.Id, linked2!.PropertyItemId);
        Assert.Equal(new DateOnly(2026, 7, 28), linked1.OpeningDate);
        Assert.Equal(new DateOnly(2026, 7, 28), linked2.OpeningDate);
    }

    private static PropertyItem MakeItem(string court, string itemNo, CaseNumber caseNumber) => new()
    {
        Id = $"{court}:{court}-{itemNo}",
        SaleUnitId = $"{court}-{itemNo}",
        CourtId = court,
        PrefectureId = "90",
        Case = caseNumber,
        ViewingStart = new DateOnly(2026, 6, 26),
        BiddingStart = new DateOnly(2026, 7, 22),
        BiddingEnd = new DateOnly(2026, 7, 27),
        OpeningDate = new DateOnly(2026, 7, 28),
        Items = [new PropertyItemDetail { ItemNo = itemNo }],
        FirstSeen = DateTimeOffset.UtcNow,
        LastSeen = DateTimeOffset.UtcNow,
    };

    private static SaleResult MakeResult(string court, string itemNo) => new()
    {
        Id = SaleResult.MakeId(court, CaseRaw, itemNo),
        CourtId = court,
        CaseLabel = CaseRaw,
        ItemNo = itemNo,
        OpeningDate = null,
        CapturedAt = DateTimeOffset.UtcNow,
    };
}
