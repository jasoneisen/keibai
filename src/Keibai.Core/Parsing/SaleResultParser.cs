using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Keibai.Core.Domain;

namespace Keibai.Core.Parsing;

/// <summary>One 売却結果 row parsed from BIT's results listing.</summary>
public sealed record SaleResultRow(
    CaseNumber? Case,
    string? CaseLabel,
    string? ItemNo,
    SaleCls? SaleCls,
    long? WinningBid,
    long? SaleStandardAmount,
    int? BidCount,
    string? Outcome,
    string? RawAddress);

/// <summary>A parsed 売却結果 page: the rows plus the pagination envelope (totalCount / page).</summary>
public sealed record SaleResultPage(
    int TotalCount, int CurrentPage, int PageSize, IReadOnlyList<SaleResultRow> Rows);

/// <summary>
/// Parses BIT's 売却結果一覧 (sale-results list) — both the first page (<c>peroidsearch/ps007/h08</c>) and
/// the pager pages (<c>resultlist/pr002/h03</c>), which share the row markup. Each row is a
/// <c>bit__currentSearchCondition_regionBox</c> carrying the 種別 badge, case number, 売却価額 (or
/// <c>-</c>), 売却基準価額, 物件番号, 開札結果, and 入札者数. Fixtures:
/// <c>saleresults_ps007_h08_tokyo.html</c>, <c>saleresults_pr002_h03_tokyo_p2.html</c>.
/// </summary>
public static partial class SaleResultParser
{
    [GeneratedRegex(@"([0-9０-９,，]+)\s*円", RegexOptions.CultureInvariant)]
    private static partial Regex Yen();

    /// <summary>Parse a full 売却結果 listing page.</summary>
    public static SaleResultPage Parse(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var total = HiddenInt(doc, "totalCount") ?? 0;
        // Page 1 (h08) has no currPage field; the pager (pr002/h03) echoes currPage.
        var current = HiddenInt(doc, "currPage") ?? HiddenInt(doc, "currentPage") ?? 1;
        var pageSize = HiddenInt(doc, "pageSize") ?? 10;

        var rows = new List<SaleResultRow>();
        var boxes = doc.DocumentNode.SelectNodes(
            "//div[contains(@class,'bit__currentSearchCondition_regionBox')]");
        if (boxes is null)
        {
            return new SaleResultPage(total, current, pageSize, rows);
        }

        foreach (var box in boxes)
        {
            var text = HtmlEntity.DeEntitize(box.InnerText) ?? string.Empty;
            var caseLabel = HeaderCaseLabel(box);
            if (caseLabel is null || !caseLabel.Contains('号'))
            {
                caseLabel = CaseLabelFromText(text);
            }

            if (caseLabel is null)
            {
                continue; // BIT reuses this class for the search-condition summary box — not a result row
            }

            rows.Add(new SaleResultRow(
                Case: CaseNumberParser.Parse(caseLabel),
                CaseLabel: caseLabel,
                ItemNo: TextAfter(text, "物件番号"),
                SaleCls: Badge(box),
                WinningBid: YenAfter(text, "売却価額"),
                SaleStandardAmount: YenAfter(text, "売却基準価額"),
                BidCount: IntAfter(text, "入札者数（人）"),
                Outcome: NormalizeOutcome(TextAfter(text, "開札結果")),
                RawAddress: Address(box)));
        }

        return new SaleResultPage(total, current, pageSize, rows);
    }

    private static string? HeaderCaseLabel(HtmlNode box)
    {
        var p = box.SelectSingleNode(".//p[contains(@class,'font-weight-bold')]");
        var t = p is null ? null : HtmlEntity.DeEntitize(p.InnerText)?.Trim();
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    private static string? CaseLabelFromText(string text)
    {
        var m = Regex.Match(
            text,
            @"(令和|平成|昭和)\s*\d+\s*年\s*[(（][^)）][)）]\s*第\s*\d+\s*号",
            RegexOptions.CultureInvariant);
        return m.Success ? m.Value : null;
    }

    private static SaleCls? Badge(HtmlNode box)
    {
        var badge = box.SelectSingleNode(".//span[contains(@class,'badge')]");
        return (badge?.InnerText.Trim()) switch
        {
            "土地" => SaleCls.Land,
            "戸建" => SaleCls.Detached,
            "マンション" => SaleCls.Mansion,
            "その他" => SaleCls.Other,
            _ => null,
        };
    }

    private static string? Address(HtmlNode box)
    {
        var icon = box.SelectSingleNode(".//i[contains(@class,'bit__icon_access')]");
        var text = icon?.ParentNode?.InnerText?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : HtmlEntity.DeEntitize(text).Trim();
    }

    /// <summary>Normalize BIT's 開札結果 labels to the canonical outcomes.</summary>
    private static string? NormalizeOutcome(string? raw) => raw switch
    {
        null => null,
        _ when raw.Contains("特別売却") => "特別売却",
        _ when raw.Contains("取下") => "取下げ",
        _ when raw.Contains("不売") => "不売",
        _ when raw.Contains("売却") => "売却",
        _ => raw,
    };

    /// <summary>The first non-empty token after a label in the row's flattened text (e.g. 物件番号 → "1").</summary>
    private static string? TextAfter(string text, string label)
    {
        var i = text.IndexOf(label, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        var rest = text[(i + label.Length)..];
        var token = rest.Split([' ', '\t', '\n', '\r', '　', '（', '(', ')', '）'],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) || token == "-" || token == "－" ? null : token;
    }

    private static long? YenAfter(string text, string label)
    {
        var i = text.IndexOf(label, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        var m = Yen().Match(text, i + label.Length);
        if (!m.Success || m.Index > i + label.Length + 40)
        {
            return null; // no yen amount close after the label → "-" (not sold)
        }

        var digits = new string(m.Groups[1].Value
            .Select(c => c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c)
            .Where(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? IntAfter(string text, string label)
    {
        var token = TextAfter(text, label);
        var digits = new string((token ?? string.Empty)
            .Select(c => c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c)
            .Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? HiddenInt(HtmlDocument doc, string name)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//input[@name='{name}']");
        var v = node?.GetAttributeValue<string?>("value", null);
        return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
