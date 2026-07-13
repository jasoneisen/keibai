using Keibai.Core.Bit;
using Keibai.Core.Ingestion;
using Xunit;

namespace Keibai.Tests;

public class RegionsTests
{
    [Theory]
    [InlineData("13", "03", "関東")]  // Tokyo — verified live
    [InlineData("01", "01", "北海道")]
    [InlineData("47", "09", "沖縄")]
    [InlineData("27", "05", "近畿")]  // Osaka
    public void Maps_prefecture_to_block(string prefecture, string block, string name)
    {
        var (blockCls, blockName) = Regions.BlockFor(prefecture);
        Assert.Equal(block, blockCls);
        Assert.Equal(name, blockName);
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
}
