using Keibai.Core.Domain;
using Keibai.Core.Search;
using Marten;
using Xunit;

namespace Keibai.Tests;

/// <summary>
/// DB-backed tests for the multi-select status filter (<see cref="PropertySearch.Apply"/>). Each seeded
/// property sits in a distinct lifecycle status relative to a fixed <c>today</c>, so we can assert that a
/// selected set of statuses ORs to exactly the matching ids — through the real Marten SQL translation, the
/// same path the search page and the nightly digest use.
/// </summary>
[Collection("host")]
public class PropertySearchStatusTests(HostFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 8, 10);

    // Dates chosen so RoundStatus.Derive yields each label at Today (2026-08-10).
    private static PropertyItem OfStatus(string court, string unit, BiddingStatus status) => status switch
    {
        BiddingStatus.Upcoming => Item(court, unit, view: new(2026, 8, 20), bstart: new(2026, 8, 25), bend: new(2026, 8, 30), open: new(2026, 9, 5)),
        BiddingStatus.Viewing => Item(court, unit, view: new(2026, 8, 1), bstart: new(2026, 8, 15), bend: new(2026, 8, 22), open: new(2026, 8, 29)),
        BiddingStatus.Bidding => Item(court, unit, view: new(2026, 8, 1), bstart: new(2026, 8, 5), bend: new(2026, 8, 15), open: new(2026, 8, 22)),
        BiddingStatus.Closed => Item(court, unit, view: new(2026, 8, 1), bstart: new(2026, 8, 3), bend: new(2026, 8, 8), open: new(2026, 8, 20)),
        _ => Item(court, unit, view: new(2026, 7, 20), bstart: new(2026, 7, 28), bend: new(2026, 8, 2), open: new(2026, 8, 5)),
    };

    private static PropertyItem Item(
        string court, string unit,
        DateOnly view, DateOnly bstart, DateOnly bend, DateOnly open) => new()
        {
            Id = $"{court}:{unit}",
            SaleUnitId = unit,
            CourtId = court,
            PrefectureId = "13",
            SaleCls = SaleCls.Detached,
            ViewingStart = view,
            BiddingStart = bstart,
            BiddingEnd = bend,
            OpeningDate = open,
        };

    [Fact]
    public void Derived_status_of_each_seeded_item_matches_the_label_it_was_built_for()
    {
        // Guards the fixture dates: each item genuinely sits in its intended lifecycle status at Today.
        Assert.Equal("upcoming", PropertySearch.DeriveStatus(OfStatus("c", "1", BiddingStatus.Upcoming), Today));
        Assert.Equal("viewing", PropertySearch.DeriveStatus(OfStatus("c", "1", BiddingStatus.Viewing), Today));
        Assert.Equal("bidding", PropertySearch.DeriveStatus(OfStatus("c", "1", BiddingStatus.Bidding), Today));
        Assert.Equal("closed", PropertySearch.DeriveStatus(OfStatus("c", "1", BiddingStatus.Closed), Today));
        Assert.Equal("opened", PropertySearch.DeriveStatus(OfStatus("c", "1", BiddingStatus.Opened), Today));
    }

    [Fact]
    public async Task Multi_status_filter_ORs_to_exactly_the_selected_statuses()
    {
        var court = "S" + Guid.NewGuid().ToString("N")[..8];
        var upcoming = OfStatus(court, "1", BiddingStatus.Upcoming);
        var viewing = OfStatus(court, "2", BiddingStatus.Viewing);
        var bidding = OfStatus(court, "3", BiddingStatus.Bidding);
        var closed = OfStatus(court, "4", BiddingStatus.Closed);
        var opened = OfStatus(court, "5", BiddingStatus.Opened);
        await Seed(upcoming, viewing, bidding, closed, opened);

        // Select two non-adjacent statuses — the OR must return exactly those two ids, nothing between.
        var ids = await MatchingIds(court, [BiddingStatus.Viewing, BiddingStatus.Opened]);

        Assert.Equal([viewing.Id, opened.Id], ids);
    }

    [Fact]
    public async Task Empty_and_all_five_status_sets_both_impose_no_constraint()
    {
        var court = "S" + Guid.NewGuid().ToString("N")[..8];
        var all = new[]
        {
            OfStatus(court, "1", BiddingStatus.Upcoming),
            OfStatus(court, "2", BiddingStatus.Viewing),
            OfStatus(court, "3", BiddingStatus.Bidding),
            OfStatus(court, "4", BiddingStatus.Closed),
            OfStatus(court, "5", BiddingStatus.Opened),
        };
        await Seed(all);
        var everyId = all.Select(i => i.Id).OrderBy(x => x).ToList();

        var empty = await MatchingIds(court, []);
        var allFive = await MatchingIds(court,
        [
            BiddingStatus.Upcoming, BiddingStatus.Viewing, BiddingStatus.Bidding,
            BiddingStatus.Closed, BiddingStatus.Opened,
        ]);

        Assert.Equal(everyId, empty);
        Assert.Equal(everyId, allFive);
    }

    [Fact]
    public async Task Single_status_selection_filters_to_just_that_status()
    {
        var court = "S" + Guid.NewGuid().ToString("N")[..8];
        var upcoming = OfStatus(court, "1", BiddingStatus.Upcoming);
        var closed = OfStatus(court, "2", BiddingStatus.Closed);
        await Seed(upcoming, closed);

        Assert.Equal([closed.Id], await MatchingIds(court, [BiddingStatus.Closed]));
    }

    private async Task Seed(params PropertyItem[] items)
    {
        await using var seed = fixture.Store.LightweightSession();
        seed.Store(items);
        await seed.SaveChangesAsync();
    }

    private async Task<List<string>> MatchingIds(string court, IReadOnlyList<BiddingStatus> statuses)
    {
        await using var q = fixture.Store.QuerySession();
        var query = new PropertyQuery { CourtId = court, Statuses = statuses };
        return (await PropertySearch.Apply(q.Query<PropertyItem>(), query, Today)
                .Select(x => x.Id)
                .ToListAsync())
            .OrderBy(x => x)
            .ToList();
    }
}
