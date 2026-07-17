using System.Text.Json;
using Alba;
using Keibai.Core.Domain;
using Keibai.Core.Search;
using Keibai.Web.Reading;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keibai.Tests;

/// <summary>
/// Integration tests for the map-pins read path (<see cref="PropertyReader.GetMapPinsAsync"/>) and the
/// <c>GET /jp/map-pins</c> endpoint. DB-backed via the shared Alba host so filtering goes through the real
/// Marten translation of <see cref="PropertySearch"/> — the whole point is that map and table agree.
/// </summary>
[Collection("host")]
public class MapPinsTests(HostFixture fixture)
{
    // A property with in-bounds (Tokyo-ish) BIT coordinates by default.
    private static PropertyItem Item(
        string court,
        string unit,
        string pref = "13",
        SaleCls? type = SaleCls.Detached,
        long? standard = 12_345_000,
        long? minimum = 9_876_000,
        double? lat = 35.68,
        double? lng = 139.76) => new()
        {
            Id = $"{court}:{unit}",
            SaleUnitId = unit,
            CourtId = court,
            PrefectureId = pref,
            SaleCls = type,
            RawAddress = "東京都千代田区1-2-3",
            DetailAddress = "東京都千代田区1-2-3 (地番)",
            SaleStandardAmount = standard,
            MinimumBidAmount = minimum,
            Latitude = lat,
            Longitude = lng,
            BiddingStart = new DateOnly(2026, 8, 1),
            BiddingEnd = new DateOnly(2026, 8, 8),
            OpeningDate = new DateOnly(2026, 8, 15),
        };

    private IPropertyReader Reader => fixture.Host.Services.GetRequiredService<IPropertyReader>();

    [Fact]
    public async Task Map_pins_and_search_agree_on_the_filtered_id_set()
    {
        var court = "M" + Guid.NewGuid().ToString("N")[..8];
        // Two Tokyo (pref 13) properties that match a pref filter, plus one Osaka (pref 27) that must not.
        var a = Item(court, "1");
        var b = Item(court, "2");
        var other = Item(court, "3", pref: "27");
        await Seed(a, b, other);

        var query = new PropertyQuery { CourtId = court, PrefectureId = "13", PageSize = 200 };

        // The table path (all pages) and the map path must return the same ids for coord-bearing items.
        var searchIds = await AllSearchIds(query);
        var pins = await Reader.GetMapPinsAsync(query, CancellationToken.None);
        var pinIds = pins.Pins.Select(p => $"{p.CourtId}:{p.SaleUnitId}").OrderBy(x => x).ToList();

        Assert.Equal(searchIds, pinIds);
        Assert.Equal([a.Id, b.Id], pinIds);
        Assert.Equal(2, pins.Total);
        Assert.Equal(0, pins.WithoutCoords);
        Assert.False(pins.Capped);
    }

    [Fact]
    public async Task Null_and_out_of_bounds_coords_are_excluded_and_counted_in_withoutCoords()
    {
        var court = "M" + Guid.NewGuid().ToString("N")[..8];
        var ok = Item(court, "1");                                   // in bounds → a pin
        var nullCoord = Item(court, "2", lat: null, lng: null);      // no coords → withoutCoords
        var garbage = Item(court, "3", lat: 0.0, lng: 0.0);          // (0,0) out of Japan → withoutCoords
        var oceania = Item(court, "4", lat: 35.68, lng: 200.0);      // lng past 155 → withoutCoords
        await Seed(ok, nullCoord, garbage, oceania);

        var pins = await Reader.GetMapPinsAsync(
            new PropertyQuery { CourtId = court, PageSize = 200 }, CancellationToken.None);

        Assert.Equal([ok.Id], pins.Pins.Select(p => $"{p.CourtId}:{p.SaleUnitId}").ToList());
        Assert.Equal(1, pins.Total);
        Assert.Equal(3, pins.WithoutCoords);
        Assert.False(pins.Capped);
    }

    [Fact]
    public async Task Pin_projects_the_wire_fields_from_the_item()
    {
        var court = "M" + Guid.NewGuid().ToString("N")[..8];
        var item = Item(court, "1");
        await Seed(item);

        var pin = Assert.Single(
            (await Reader.GetMapPinsAsync(new PropertyQuery { CourtId = court }, CancellationToken.None)).Pins);

        Assert.Equal(court, pin.CourtId);
        Assert.Equal("1", pin.SaleUnitId);
        Assert.Equal(35.68, pin.Lat);
        Assert.Equal(139.76, pin.Lng);
        Assert.Equal("戸建 / Detached", pin.TypeLabel);
        Assert.Equal("東京都千代田区1-2-3 (地番)", pin.Address);   // DetailAddress preferred over RawAddress
        Assert.Equal(12_345_000, pin.Price);
        Assert.Equal(9_876_000, pin.MinBid);
        Assert.Equal(new DateOnly(2026, 8, 8), pin.BiddingEnd);
    }

    [Fact]
    public async Task Endpoint_returns_camelCase_json_with_iso_dates()
    {
        var court = "M" + Guid.NewGuid().ToString("N")[..8];
        await Seed(Item(court, "1"));

        var result = await fixture.Host.Scenario(s =>
        {
            s.Get.Url($"/jp/map-pins?court={court}");
            s.StatusCodeShouldBeOk();
        });

        var body = result.ReadAsText();

        // Envelope + pin members are camelCase; the DateOnly is ISO yyyy-MM-dd.
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("total").GetInt64());
        Assert.Equal(0, root.GetProperty("withoutCoords").GetInt64());
        Assert.False(root.GetProperty("capped").GetBoolean());

        var pin = root.GetProperty("pins")[0];
        Assert.Equal(court, pin.GetProperty("courtId").GetString());
        Assert.Equal("1", pin.GetProperty("saleUnitId").GetString());
        Assert.Equal(35.68, pin.GetProperty("lat").GetDouble());
        Assert.Equal(139.76, pin.GetProperty("lng").GetDouble());
        Assert.Equal("2026-08-08", pin.GetProperty("biddingEnd").GetString());
        Assert.Equal(12_345_000, pin.GetProperty("price").GetInt64());

        // camelCase contract: no PascalCase leakage.
        Assert.DoesNotContain("\"CourtId\"", body);
        Assert.DoesNotContain("\"WithoutCoords\"", body);
    }

    private async Task Seed(params PropertyItem[] items)
    {
        await using var seed = fixture.Store.LightweightSession();
        seed.Store(items);
        await seed.SaveChangesAsync();
    }

    private async Task<List<string>> AllSearchIds(PropertyQuery query)
    {
        var ids = new List<string>();
        for (var page = 1; ; page++)
        {
            var pageResult = await Reader.SearchAsync(query with { Page = page }, CancellationToken.None);
            ids.AddRange(pageResult.Items.Select(i => i.Id));
            if (page >= pageResult.TotalPages)
            {
                break;
            }
        }

        return ids.OrderBy(x => x).ToList();
    }
}
