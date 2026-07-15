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
    long? MinimumBidAmount,
    IReadOnlyList<PropertyItemDetail> Items);

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
        var items = ParseItems(doc);

        return new PropertyDetail(
            saleUnitId, courtId, caseNo, lat, lng, hasPdf,
            saleCls, viewingStart, bidStart, bidEnd, openingDate, saleDecision, saleStandard, minimumBid, items);
    }

    /// <summary>
    /// Parse every 物件's full attribute set from the detail page's 物件明細 tables
    /// (<c>bit__paragraphBreaksTable_th</c> label → <c>_td</c> value). A new item begins at each 種別 row.
    /// Captures ALL labels (nothing dropped), normalizing half/full-width parens so 構造(現況) and
    /// 構造（現況） are one key.
    /// </summary>
    public static List<PropertyItemDetail> ParseItems(HtmlDocument doc)
    {
        var items = new List<PropertyItemDetail>();
        var ths = doc.DocumentNode.SelectNodes("//div[contains(@class,'bit__paragraphBreaksTable_th')]");
        if (ths is null)
        {
            return items;
        }

        PropertyItemDetail? current = null;
        foreach (var th in ths)
        {
            var label = NormalizeLabel(HtmlEntity.DeEntitize(th.InnerText).Trim());
            if (label.Length == 0)
            {
                continue;
            }

            var td = th.SelectSingleNode(
                "following-sibling::div[contains(@class,'bit__paragraphBreaksTable_td')][1]");
            var value = TdValue(td);

            if (label == "種別" || current is null)
            {
                current = new PropertyItemDetail { Kind = label == "種別" ? value : null };
                items.Add(current);
            }

            if (label == "物件番号")
            {
                current.ItemNo = value;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                current.Attributes[label] = value;
            }
        }

        return items;
    }

    /// <summary>
    /// Copy the parsed <paramref name="items"/> onto the property and derive typed rollups (first non-empty
    /// value across the bundled 物件 — so a 戸建 card's land item supplies land area and its building item
    /// supplies floor area / 築年月).
    /// </summary>
    public static void ApplyAttributeRollups(PropertyItem item, IReadOnlyList<PropertyItemDetail> items)
    {
        item.Items = items.ToList();
        item.ItemCount = items.Count;

        string? Attr(string label) => items
            .Select(i => i.Attributes.GetValueOrDefault(label))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        item.DetailAddress = Attr("所在地") ?? item.DetailAddress;
        item.HouseNumber = Attr("家屋番号") ?? item.HouseNumber;
        item.LandCategory = Attr("地目（登記）") ?? item.LandCategory;
        item.ZoningUse = Attr("用途地域") ?? item.ZoningUse;
        item.BuildingCoverageRatio = Attr("建ぺい率") ?? item.BuildingCoverageRatio;
        item.FloorAreaRatio = Attr("容積率") ?? item.FloorAreaRatio;
        item.Structure = Attr("構造（登記）") ?? item.Structure;
        item.RoomLayout = Attr("間取り") ?? item.RoomLayout;
        item.LandRights = Attr("敷地利用権") ?? item.LandRights;
        item.Occupant = Attr("占有者") ?? item.Occupant;
        item.Floor = Attr("階") ?? item.Floor;
        item.BuildYearMonth = Attr("築年月") ?? item.BuildYearMonth;
        item.BuildYear = JapaneseDate.Year(item.BuildYearMonth) ?? item.BuildYear;
        item.TotalUnits = ParseInt(Attr("総戸数")) ?? item.TotalUnits;
        item.AdminFeeYen = ParseYenString(Attr("管理費等")) ?? item.AdminFeeYen;
        item.LandAreaSqm = ParseArea(Attr("土地面積（登記）")) ?? item.LandAreaSqm;
        item.BuildingAreaSqm = ParseArea(Attr("床面積（登記）")) ?? item.BuildingAreaSqm;
        item.ExclusiveAreaSqm = ParseArea(Attr("専有面積（登記）")) ?? item.ExclusiveAreaSqm;
    }

    private static string TdValue(HtmlNode? td)
    {
        if (td is null)
        {
            return string.Empty;
        }

        var span = td.SelectNodes(".//span")?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.InnerText));
        var raw = span?.InnerText ?? td.InnerText;
        return System.Text.RegularExpressions.Regex.Replace(
            HtmlEntity.DeEntitize(raw ?? string.Empty).Trim(), @"\s+", " ");
    }

    // Normalize half-width ()/() parens to full-width so 構造(現況) and 構造（現況） collapse to one label.
    private static string NormalizeLabel(string label) =>
        label.Replace('(', '（').Replace(')', '）');

    private static double? ParseArea(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // e.g. "８８．１７m", "１階 ４６．７５m" → the number immediately before m.
        var m = System.Text.RegularExpressions.Regex.Match(Ascii(value), @"([0-9]+(?:\.[0-9]+)?)\s*m");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    private static int? ParseInt(string? value)
    {
        var digits = new string((value ?? string.Empty).Select(Ascii).Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var v) ? v : null;
    }

    private static long? ParseYenString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var m = Yen().Match(Ascii(value));
        var digits = m.Success ? m.Groups[1].Value.Replace(",", string.Empty) : string.Empty;
        return long.TryParse(digits, out var v) ? v : null;
    }

    private static string Ascii(string s) => new(s.Select(Ascii).ToArray());

    private static char Ascii(char c) => c switch
    {
        >= '０' and <= '９' => (char)('0' + (c - '０')),
        '．' => '.',
        '，' => ',',
        _ => c,
    };

    /// <summary>
    /// The card-level property type from the detail page's header <c>badge</c> span (土地 / 戸建て /
    /// マンション / その他) — NOT the per-物件 種別 table, whose first item is often 土地 even on a 戸建 card.
    /// </summary>
    private static SaleCls? ParseSaleCls(HtmlDocument doc)
    {
        var badge = doc.DocumentNode.SelectSingleNode("//span[contains(@class,'badge')]");
        return SaleClassifier.Parse(badge?.InnerText);
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
