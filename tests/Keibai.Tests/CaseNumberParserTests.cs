using Keibai.Core.Domain;
using Keibai.Core.Parsing;
using Xunit;

namespace Keibai.Tests;

public class CaseNumberParserTests
{
    [Theory]
    [InlineData("東京地方裁判所立川支部　令和08年(ヌ)第12号", "令和", 8, CaseType.Nu, 12)]
    [InlineData("令和7年(ケ)第123号", "令和", 7, CaseType.Ke, 123)]
    [InlineData("平成30年（ケ）第5号", "平成", 30, CaseType.Ke, 5)]
    [InlineData("令和０８年(ヌ)第１２号", "令和", 8, CaseType.Nu, 12)]
    public void Parses_structured_case_numbers(string text, string era, int year, CaseType type, int serial)
    {
        var result = CaseNumberParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal(era, result!.Era);
        Assert.Equal(year, result.Year);
        Assert.Equal(type, result.Type);
        Assert.Equal(serial, result.Serial);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no case here")]
    public void Returns_null_when_no_case(string? text) => Assert.Null(CaseNumberParser.Parse(text));
}
