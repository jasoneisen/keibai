using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Keibai.Core.Parsing;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Keibai.Tests;

[Collection("host")]
public class ResultsHandlerTests(HostFixture fixture)
{
    [Fact]
    public async Task Backfill_upsert_is_idempotent_on_court_case_item()
    {
        var courtId = "77" + Guid.NewGuid().ToString("N")[..3]; // isolated test court
        var rows = SaleResultParser.Parse(Fixtures.SaleResultsListing()).Rows;
        Assert.NotEmpty(rows);
        var store = fixture.Host.Services.GetRequiredService<IKeibaiStoreAccessor>();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-15T20:00:00+09:00"));

        // First pass creates one SaleResult per (court, case, item).
        await ResultsHandler.UpsertResultsAsync(store, courtId, rows, time, CancellationToken.None);
        // Second pass with identical rows must upsert, not duplicate.
        await ResultsHandler.UpsertResultsAsync(store, courtId, rows, time, CancellationToken.None);

        await using var q = fixture.Store.QuerySession();
        var stored = await q.Query<SaleResult>().Where(r => r.CourtId == courtId).CountAsync();
        var expected = rows.Select(r => SaleResult.MakeId(courtId, r.CaseLabel, r.ItemNo)).Distinct().Count();
        Assert.Equal(expected, stored);

        // A sold row round-trips its winning bid + outcome.
        var sold = await q.Query<SaleResult>()
            .Where(r => r.CourtId == courtId && r.Outcome == "売却" && r.WinningBid > 0).ToListAsync();
        Assert.NotEmpty(sold);
    }
}
