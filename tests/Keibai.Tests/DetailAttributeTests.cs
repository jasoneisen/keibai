using Keibai.Core.Domain;
using Keibai.Core.Parsing;
using Xunit;

namespace Keibai.Tests;

public class DetailAttributeTests
{
    private static PropertyItem Enrich(string html)
    {
        var detail = DetailParser.Parse(html);
        var item = new PropertyItem { Id = "c:s", SaleUnitId = "s", CourtId = "c", PrefectureId = "13" };
        DetailParser.ApplyAttributeRollups(item, detail.Items);
        return item;
    }

    [Fact]
    public void Kodate_multi_item_card_rolls_up_land_and_building_attributes()
    {
        // 物件1 = 土地 (88.17 m²), 物件2 = 建物 (木造3階, 46.75 m², ３ＬＤＫ, 築 平成29年6月).
        var item = Enrich(Fixtures.DetailKodate());

        Assert.Equal(2, item.ItemCount);
        Assert.Equal(88.17, item.LandAreaSqm);
        Assert.Equal(46.75, item.BuildingAreaSqm);
        Assert.Contains("木造", item.Structure!);
        Assert.StartsWith("３ＬＤＫ", item.RoomLayout);
        Assert.Equal("平成29年6月", item.BuildYearMonth);
        Assert.Equal(2017, item.BuildYear);
        Assert.Equal("所有権", item.LandRights);
        Assert.Equal("あり", item.Occupant);
        Assert.Equal("第一種中高層住居専用地域", item.ZoningUse);
        Assert.Equal("60%", item.BuildingCoverageRatio);

        // The two 物件 kinds are captured distinctly.
        Assert.Equal(["土地", "建物"], item.Items.Select(i => i.Kind));
    }

    [Fact]
    public void Mansion_attributes_are_captured()
    {
        var item = Enrich(Fixtures.DetailMansion());

        Assert.Equal(1, item.ItemCount);
        Assert.Equal(52.32, item.ExclusiveAreaSqm);
        Assert.Equal(28_270, item.AdminFeeYen);
        Assert.Equal(1996, item.BuildYear); // 平成8年11月
        Assert.Equal(21, item.TotalUnits);
        Assert.Equal("３階", item.Floor);
        Assert.Contains("鉄筋コンクリート", item.Structure!);
        Assert.StartsWith("２ＬＤＫ", item.RoomLayout);
        Assert.Equal("債務者・所有者", item.Occupant);
        Assert.Equal("区分所有建物", item.Items[0].Kind);
    }

    [Fact]
    public void Land_only_card_has_land_area_and_no_building_fields()
    {
        var item = Enrich(Fixtures.Detail());

        Assert.Equal(3, item.ItemCount); // three 土地 parcels bundled in one sale unit
        Assert.All(item.Items, i => Assert.Equal("土地", i.Kind));
        Assert.Equal(477, item.LandAreaSqm); // first parcel
        Assert.Null(item.BuildingAreaSqm);
        Assert.Null(item.ExclusiveAreaSqm);
        Assert.Null(item.BuildYear);
    }

    [Fact]
    public void Generic_attribute_map_captures_everything_including_untyped_labels()
    {
        var item = Enrich(Fixtures.DetailMansion());
        var attrs = item.Items[0].Attributes;

        // Typed AND untyped labels are all present in the raw map — nothing dropped.
        Assert.Contains("専有面積（登記）", attrs.Keys);
        Assert.Contains("家屋番号", attrs.Keys);   // not promoted to a rollup on mansion, still captured
        Assert.Contains("種類（登記）", attrs.Keys);
        Assert.Contains("敷地利用権", attrs.Keys);
        Assert.Equal("居宅", attrs["種類（登記）"]);
    }

    [Fact]
    public void Half_and_full_width_paren_labels_collapse_to_one_key()
    {
        // 戸建 fixture renders both 構造(現況) and 構造（現況） — the parser must not create two keys.
        var item = Enrich(Fixtures.DetailKodate());
        var buildingItem = item.Items.Single(i => i.Kind == "建物");
        Assert.Contains("構造（現況）", buildingItem.Attributes.Keys);
        Assert.DoesNotContain("構造(現況)", buildingItem.Attributes.Keys);
    }
}
