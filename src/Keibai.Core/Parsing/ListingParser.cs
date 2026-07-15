using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Keibai.Core.Domain;

namespace Keibai.Core.Parsing;

/// <summary>One property row parsed from a BIT result-listing page.</summary>
public sealed record ListingRow(
    string SaleUnitId,
    string CourtId,
    string? CourtName,
    CaseNumber? Case,
    SaleCls? SaleCls,
    string? RawAddress,
    long? SaleStandardAmount,
    long? MinimumBidDeposit);

/// <summary>The parsed contents of a result-listing page: rows plus the pagination envelope.</summary>
public sealed record ListingPage(int TotalCount, int CurrentPage, int PageSize, IReadOnlyList<ListingRow> Rows);

/// <summary>
/// Parses BIT's 競売物件検索：結果一覧 page (fixture <c>results_ps002_h05_tokyo.html</c>). Each row is
/// keyed by the <c>tranPropertyDetail(saleUnitId, courtId, tab)</c> anchor; the court name and case
/// number come from the anchor text; prices from the 価額 container.
/// </summary>
public static partial class ListingParser
{
    // The onclick attribute survives in InnerHtml with entity-encoded quotes (&quot; / &#39;), so match a
    // run of digits between any quote form.
    [GeneratedRegex(@"tranPropertyDetail\(\s*(?:""|'|&quot;|&#39;)?(\d+)(?:""|'|&quot;|&#39;)?\s*,\s*(?:""|'|&quot;|&#39;)?(\d+)(?:""|'|&quot;|&#39;)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex DetailCall();

    [GeneratedRegex(@"([0-9,]+)\s*円", RegexOptions.CultureInvariant)]
    private static partial Regex Yen();

    /// <summary>Parse a full result-listing page.</summary>
    public static ListingPage Parse(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var total = HiddenInt(doc, "totalCount") ?? 0;
        var current = HiddenInt(doc, "currentPage") ?? 1;
        var pageSize = HiddenInt(doc, "pageSize") ?? 10;

        var rows = new List<ListingRow>();
        var seen = new HashSet<string>();

        // Each result card is a .bit__searchResult block; use the first tranPropertyDetail anchor as
        // the row anchor and dedupe by (courtId, saleUnitId) since the anchor repeats within a card.
        var cards = doc.DocumentNode.SelectNodes("//div[contains(@class,'bit__searchResult')]");
        if (cards is null)
        {
            return new ListingPage(total, current, pageSize, rows);
        }

        foreach (var card in cards)
        {
            var inner = card.InnerHtml;
            var call = DetailCall().Match(inner);
            if (!call.Success)
            {
                continue;
            }

            var saleUnitId = call.Groups[1].Value;
            var courtId = call.Groups[2].Value;
            var key = $"{courtId}:{saleUnitId}";
            if (!seen.Add(key))
            {
                continue;
            }

            var anchorText = DecodeText(FirstDetailAnchorText(card));
            var caseNo = CaseNumberParser.Parse(anchorText);
            var courtName = ExtractCourtName(anchorText);
            var saleCls = ExtractSaleCls(card);
            var (standard, deposit) = ExtractPrices(card);
            var address = ExtractAddress(card);

            rows.Add(new ListingRow(
                saleUnitId, courtId, courtName, caseNo, saleCls, address, standard, deposit));
        }

        return new ListingPage(total, current, pageSize, rows);
    }

    private static string? FirstDetailAnchorText(HtmlNode card)
    {
        var anchors = card.SelectNodes(".//a[contains(@onclick,'tranPropertyDetail')]");
        if (anchors is null)
        {
            return null;
        }

        foreach (var a in anchors)
        {
            var t = a.InnerText?.Trim();
            if (!string.IsNullOrWhiteSpace(t))
            {
                return t;
            }
        }

        return null;
    }

    private static string? ExtractCourtName(string? anchorText)
    {
        if (string.IsNullOrWhiteSpace(anchorText))
        {
            return null;
        }

        // "東京地方裁判所立川支部　令和08年(ヌ)第12号" — court name is everything before the case number,
        // split on the full-width or half-width space that precedes 令和/平成/昭和.
        var idx = anchorText.IndexOfAny(['令', '平', '昭']);
        var name = idx > 0 ? anchorText[..idx] : anchorText;
        return name.Trim(' ', '　', '\t', '\n', '\r');
    }

    private static SaleCls? ExtractSaleCls(HtmlNode card)
    {
        var badge = card.SelectSingleNode(".//span[contains(@class,'badge')]");
        return SaleClassifier.Parse(badge?.InnerText);
    }

    private static (long? Standard, long? Deposit) ExtractPrices(HtmlNode card)
    {
        long? standard = null;
        long? deposit = null;

        var labels = card.SelectNodes(".//p[contains(@class,'bit__text_small')]");
        if (labels is not null)
        {
            foreach (var label in labels)
            {
                var text = label.InnerText.Trim();
                var valueNode = label.SelectSingleNode("following-sibling::p[1]");
                var yen = ParseYen(valueNode?.InnerText);
                if (yen is null)
                {
                    continue;
                }

                if (text.Contains("売却基準価額"))
                {
                    standard = yen;
                }
                else if (text.Contains("買受申出保証金"))
                {
                    deposit = yen;
                }
            }
        }

        return (standard, deposit);
    }

    private static string? ExtractAddress(HtmlNode card)
    {
        var icon = card.SelectSingleNode(".//i[contains(@class,'bit__icon_access')]");
        var p = icon?.ParentNode;
        var text = p?.InnerText?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : DecodeText(text);
    }

    private static long? ParseYen(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = Yen().Match(text);
        if (!m.Success)
        {
            return null;
        }

        var digits = m.Groups[1].Value.Replace(",", string.Empty);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? HiddenInt(HtmlDocument doc, string name)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//input[@name='{name}']");
        var v = node?.GetAttributeValue<string?>("value", null);
        return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string DecodeText(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : HtmlEntity.DeEntitize(s).Trim();
}
