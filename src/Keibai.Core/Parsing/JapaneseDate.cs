using System.Globalization;
using System.Text.RegularExpressions;

namespace Keibai.Core.Parsing;

/// <summary>
/// Parses Japanese imperial-era (和暦) calendar dates as BIT renders them, e.g.
/// <c>令和08年07月22日</c> → <c>2026-07-22</c>. Only the eras that appear in live auction data are
/// supported (令和 / 平成 / 昭和); the day and month are always explicit on the page so era-boundary
/// months never arise in practice.
/// </summary>
public static partial class JapaneseDate
{
    // 令和/平成/昭和 (optional 元 for year 1) NN年 NN月 NN日 — full-width digits are normalized first.
    [GeneratedRegex(@"(令和|平成|昭和)\s*(元|\d{1,2})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日",
        RegexOptions.CultureInvariant)]
    private static partial Regex EraDate();

    // Gregorian year of each era's year 1, minus 1 (so gregorian = base + eraYear).
    private static readonly Dictionary<string, int> EraBase = new(StringComparer.Ordinal)
    {
        ["令和"] = 2018, // 令和1 = 2019
        ["平成"] = 1988, // 平成1 = 1989
        ["昭和"] = 1925, // 昭和1 = 1926
    };

    /// <summary>
    /// Parse the FIRST 和暦 date found in <paramref name="text"/>. Returns null when none is present or the
    /// components are out of range.
    /// </summary>
    public static DateOnly? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var normalized = NormalizeDigits(text);
        var m = EraDate().Match(normalized);
        if (!m.Success || !EraBase.TryGetValue(m.Groups[1].Value, out var eraBase))
        {
            return null;
        }

        var eraYear = m.Groups[2].Value == "元"
            ? 1
            : int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        var year = eraBase + eraYear;

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new DateOnly(year, month, day);
    }

    /// <summary>
    /// Parse a 入札期間 range like <c>令和08年07月15日 〜 令和08年07月22日</c> into (start, end). Either side
    /// may be null when unparseable.
    /// </summary>
    public static (DateOnly? Start, DateOnly? End) ParseRange(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (null, null);
        }

        var normalized = NormalizeDigits(text);
        var matches = EraDate().Matches(normalized);
        var start = matches.Count > 0 ? Parse(matches[0].Value) : null;
        var end = matches.Count > 1 ? Parse(matches[1].Value) : null;
        return (start, end);
    }

    /// <summary>Map full-width digits (０–９) to ASCII so the numeric groups match.</summary>
    private static string NormalizeDigits(string s)
    {
        Span<char> buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buffer[i] = c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c;
        }

        return new string(buffer);
    }
}
