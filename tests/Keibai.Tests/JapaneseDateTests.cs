using Keibai.Core.Parsing;
using Xunit;

namespace Keibai.Tests;

public class JapaneseDateTests
{
    [Theory]
    [InlineData("令和08年07月22日", 2026, 7, 22)]
    [InlineData("令和8年7月22日", 2026, 7, 22)]
    [InlineData("令和元年05月01日", 2019, 5, 1)]
    [InlineData("平成31年04月30日", 2019, 4, 30)]
    [InlineData("昭和64年01月07日", 1989, 1, 7)]
    [InlineData("閲覧開始日 令和08年06月26日", 2026, 6, 26)]
    public void Parses_wareki_dates(string text, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), JapaneseDate.Parse(text));
    }

    [Theory]
    [InlineData("令和８年７月２２日", 2026, 7, 22)] // full-width digits
    public void Parses_fullwidth_digits(string text, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), JapaneseDate.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no date here")]
    [InlineData("令和08年13月40日")] // out of range
    public void Returns_null_when_absent_or_invalid(string? text)
    {
        Assert.Null(JapaneseDate.Parse(text));
    }

    [Fact]
    public void Parses_bidding_period_range()
    {
        var (start, end) = JapaneseDate.ParseRange("令和08年07月15日 〜 令和08年07月22日");
        Assert.Equal(new DateOnly(2026, 7, 15), start);
        Assert.Equal(new DateOnly(2026, 7, 22), end);
    }

    [Fact]
    public void Range_end_is_null_when_only_one_date()
    {
        var (start, end) = JapaneseDate.ParseRange("令和08年07月15日");
        Assert.Equal(new DateOnly(2026, 7, 15), start);
        Assert.Null(end);
    }
}
