using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Keibai.Core.Domain;

namespace Keibai.Core.Parsing;

/// <summary>Fields lifted from a BIT property-detail page (競売物件検索：詳細).</summary>
public sealed record PropertyDetail(
    string SaleUnitId,
    string CourtId,
    CaseNumber? Case,
    double? Latitude,
    double? Longitude,
    bool HasThreeSetPdf,
    SaleCls? SaleCls,
    DateOnly? ViewingStart,
    DateOnly? BiddingStart,
    DateOnly? BiddingEnd,
    DateOnly? OpeningDate,
    DateOnly? SaleDecisionDate,
    long? SaleStandardAmount,
    long? MinimumBidAmount);

/// <summary>Parses BIT's property-detail page (fixture <c>detail_pr001_h05.html</c>).</summary>
public static partial class DetailParser
{
    [GeneratedRegex(@"([0-9０-９,，]+)\s*円", RegexOptions.CultureInvariant)]
    private static partial Regex Yen();

    /// <summary>Parse a detail page.</summary>
    public static PropertyDetail Parse(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var saleUnitId = Hidden(doc, "saleUnitId") ?? string.Empty;
        var courtId = Hidden(doc, "courtId") ?? string.Empty;
        var lat = HiddenDouble(doc, "latitude");
        var lng = HiddenDouble(doc, "longitude");
        var hasPdf = doc.DocumentNode.SelectSingleNode("//*[@id='threeSetPDF']") is not null;

        var text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText) ?? string.Empty;
        var caseNo = CaseNumberParser.Parse(text);

        // Bidding schedule. Each label sits immediately before its 和暦 date in the rendered text; the same
        // words recur later in tooltip prose (e.g. "売却決定期日までに…") with NO trailing date, so we scan
        // every occurrence and keep the first that actually yields a date.
        var viewingStart = DateAfterLabel(text, "閲覧開始日");
        var (bidStart, bidEnd) = RangeAfterLabel(text, "入札期間");
        var openingDate = DateAfterLabel(text, "開札期日");
        var saleDecision = DateAfterLabel(text, "売却決定期日");

        var saleCls = ParseSaleCls(doc);
        var saleStandard = YenAfterLabel(text, "売却基準価額");
        var minimumBid = YenAfterLabel(text, "買受可能価額");

        return new PropertyDetail(
            saleUnitId, courtId, caseNo, lat, lng, hasPdf,
            saleCls, viewingStart, bidStart, bidEnd, openingDate, saleDecision, saleStandard, minimumBid);
    }

    /// <summary>
    /// Recover the property type from the detail page's 種別 row
    /// (<c>&lt;div class="…_th"&gt;種別&lt;/div&gt;&lt;div class="…_td"&gt;&lt;span&gt;土地&lt;/span&gt;…</c>).
    /// Used to backfill <see cref="SaleCls"/> on multi-item listing cards whose badge markup omits it.
    /// </summary>
    private static SaleCls? ParseSaleCls(HtmlDocument doc)
    {
        var th = doc.DocumentNode.SelectNodes("//div[contains(@class,'bit__paragraphBreaksTable_th')]")
            ?.FirstOrDefault(n => n.InnerText.Trim() == "種別");
        var td = th?.SelectSingleNode("following-sibling::div[contains(@class,'bit__paragraphBreaksTable_td')][1]");
        var value = td?.SelectSingleNode(".//span")?.InnerText.Trim();
        return value switch
        {
            "土地" => Domain.SaleCls.Land,
            "戸建" => Domain.SaleCls.Detached,
            "マンション" => Domain.SaleCls.Mansion,
            "その他" => Domain.SaleCls.Other,
            _ => null,
        };
    }

    private static DateOnly? DateAfterLabel(string text, string label)
    {
        foreach (var window in WindowsAfter(text, label))
        {
            var d = JapaneseDate.Parse(window);
            if (d is not null)
            {
                return d;
            }
        }

        return null;
    }

    private static (DateOnly? Start, DateOnly? End) RangeAfterLabel(string text, string label)
    {
        foreach (var window in WindowsAfter(text, label))
        {
            var (start, end) = JapaneseDate.ParseRange(window);
            if (start is not null)
            {
                return (start, end);
            }
        }

        return (null, null);
    }

    private static long? YenAfterLabel(string text, string label)
    {
        foreach (var window in WindowsAfter(text, label))
        {
            var m = Yen().Match(window);
            if (m.Success)
            {
                var digits = new string(m.Groups[1].Value
                    .Select(c => c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c)
                    .Where(c => c is >= '0' and <= '9')
                    .ToArray());
                if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                {
                    return v;
                }
            }
        }

        return null;
    }

    /// <summary>Yield the ~60-char window immediately following each occurrence of <paramref name="label"/>.</summary>
    private static IEnumerable<string> WindowsAfter(string text, string label)
    {
        var from = 0;
        while (true)
        {
            var i = text.IndexOf(label, from, StringComparison.Ordinal);
            if (i < 0)
            {
                yield break;
            }

            var start = i + label.Length;
            var len = Math.Min(60, text.Length - start);
            yield return text.Substring(start, len);
            from = start;
        }
    }

    private static string? Hidden(HtmlDocument doc, string idOrName)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//input[@id='{idOrName}']")
                   ?? doc.DocumentNode.SelectSingleNode($"//input[@name='{idOrName}']");
        var v = node?.GetAttributeValue<string?>("value", null);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static double? HiddenDouble(HtmlDocument doc, string idOrName)
    {
        var v = Hidden(doc, idOrName);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
