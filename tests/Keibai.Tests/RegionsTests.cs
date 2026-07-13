using Keibai.Core.Bit;
using Keibai.Core.Ingestion;
using Xunit;

namespace Keibai.Tests;

public class RegionsTests
{
    [Theory]
    [InlineData("13", "03", "関東")]         // Tokyo — verified live
    [InlineData("91", "01", "北海道")]        // Sapporo pseudo-code — verified live 2026-07-13
    [InlineData("94", "01", "北海道")]        // Kushiro pseudo-code
    [InlineData("15", "04", "北陸・甲信越")]  // Niigata — verified live 2026-07-13
    [InlineData("24", "05", "東海")]          // Mie — verified live 2026-07-13
    [InlineData("27", "06", "近畿")]          // Osaka — verified live 2026-07-13
    [InlineData("32", "07", "中国")]          // Shimane
    [InlineData("47", "09", "九州・沖縄")]    // Okinawa — one combined block on BIT
    public void Maps_prefecture_to_block(string prefecture, string block, string name)
    {
        var (blockCls, blockName) = Regions.BlockFor(prefecture);
        Assert.Equal(block, blockCls);
        Assert.Equal(name, blockName);
    }

    [Fact]
    public void Hokkaido_jis_code_is_rejected()
    {
        // BIT has no prefecturesId=01 — it answers with its エラー page. The sweep must fan out over
        // the four district pseudo-codes instead, so treating "01" as valid is always a bug.
        Assert.Throws<ArgumentOutOfRangeException>(() => Regions.BlockFor("01"));
    }

    [Fact]
    public void Sweep_covers_hokkaido_districts_and_46_jis_prefectures()
    {
        Assert.Equal(50, Prefectures.All.Count);
        Assert.DoesNotContain("01", Prefectures.All);
        Assert.All(new[] { "91", "92", "93", "94", "02", "47" }, p => Assert.Contains(p, Prefectures.All));
    }

    [Fact]
    public void Every_prefecture_has_a_block()
    {
        foreach (var prefecture in Prefectures.All)
        {
            var (block, name) = Regions.BlockFor(prefecture);
            Assert.NotEmpty(block);
            Assert.NotEmpty(name);
        }
    }

    [Fact]
    public void Search_body_carries_the_prefectures_block()
    {
        var pairs = BitForms.PrefectureSearchPairs("13", 1, 10);
        Assert.Contains(pairs, p => p is { Key: "blockCls", Value: "03" });
        Assert.Contains(pairs, p => p is { Key: "blockName", Value: "関東" });
        Assert.Contains(pairs, p => p is { Key: "prefecturesId", Value: "13" });
        // The Spring checkbox-marker fields the backend requires (a partial body 500s).
        Assert.Contains(pairs, p => p.Key == "_detailAreaInfoDto.landConditionClsList");
    }

    [Fact]
    public void Error_page_is_detected_and_real_pages_are_not()
    {
        // The captured response BIT returned for the invalid prefecturesId=01 search.
        Assert.True(BitErrorPage.IsErrorPage(Fixtures.Read("error_page.html")));
        // Real pages — including an empty-result listing — must never trip the detector.
        Assert.False(BitErrorPage.IsErrorPage(Fixtures.TokyoResults()));
        Assert.False(BitErrorPage.IsErrorPage(Fixtures.Detail()));
    }
}
