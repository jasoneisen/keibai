using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Keibai.Core.Parsing;
using Marten;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Keibai.Tests;

[Collection("host")]
public class IngestionIdempotencyTests(HostFixture fixture)
{
    [Fact]
    public async Task Double_handling_the_same_listing_creates_no_duplicates()
    {
        var page = ListingParser.Parse(Fixtures.TokyoResults());
        var rows = page.Rows;
        Assert.NotEmpty(rows);

        var time = new FakeTimeProvider();
        var prefecture = "13";

        // First pass: everything is new.
        await using (var session = fixture.Store.LightweightSession())
        {
            foreach (var row in rows)
            {
                var (isNew, _) = await IngestionHandlers.UpsertRowAsync(session, prefecture, row, time);
                Assert.True(isNew);
            }

            await session.SaveChangesAsync();
        }

        // Second pass: same rows, nothing changed → no new, no dupes.
        await using (var session = fixture.Store.LightweightSession())
        {
            foreach (var row in rows)
            {
                var (isNew, changed) = await IngestionHandlers.UpsertRowAsync(session, prefecture, row, time);
                Assert.False(isNew);
                Assert.False(changed);
            }

            await session.SaveChangesAsync();
        }

        // The store holds exactly one PropertyItem per (court, saleUnit) — no duplication.
        await using var query = fixture.Store.QuerySession();
        var expectedItems = rows.Select(r => $"{r.CourtId}:{r.SaleUnitId}").Distinct().Count();
        var itemCount = await query.Query<PropertyItem>()
            .Where(p => p.PrefectureId == prefecture)
            .CountAsync();
        Assert.Equal(expectedItems, itemCount);

        var expectedCourts = rows.Select(r => r.CourtId).Distinct().Count();
        var courtCount = await query.Query<Court>()
            .Where(c => c.PrefectureId == prefecture)
            .CountAsync();
        Assert.Equal(expectedCourts, courtCount);
    }
}
