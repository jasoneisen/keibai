using System.Globalization;
using System.Text.RegularExpressions;
using Keibai.Core.Domain;

namespace Keibai.Core.Parsing;

/// <summary>
/// Parses Japanese auction case numbers like 令和08年(ヌ)第12号 / 平成30年(ケ)第123号 into a
/// structured <see cref="CaseNumber"/>. Tolerant of full-width digits and whitespace.
/// </summary>
public static partial class CaseNumberParser
{
    // era (令和/平成/昭和) + year + (ケ|ヌ) + serial
    [GeneratedRegex(@"(令和|平成|昭和)\s*([0-9０-９]+)\s*年\s*[\(（]\s*([ケヌ])\s*[\)）]\s*第\s*([0-9０-９]+)\s*号",
        RegexOptions.CultureInvariant)]
    private static partial Regex CaseRegex();

    /// <summary>Returns the parsed case number, or null when the text contains no recognisable case.</summary>
    public static CaseNumber? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = CaseRegex().Match(text);
        if (!m.Success)
        {
            return null;
        }

        var era = m.Groups[1].Value;
        var year = ParseInt(m.Groups[2].Value);
        var type = m.Groups[3].Value == "ケ" ? CaseType.Ke : CaseType.Nu;
        var serial = ParseInt(m.Groups[4].Value);
        var raw = Normalize(m.Value);
        return new CaseNumber(era, year, type, serial, raw);
    }

    private static int ParseInt(string s) =>
        int.Parse(ToHalfWidthDigits(s), CultureInfo.InvariantCulture);

    private static string Normalize(string s) =>
        WhitespaceRegex().Replace(ToHalfWidthDigits(s), string.Empty);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private static string ToHalfWidthDigits(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c;
        }

        return new string(buf);
    }
}
