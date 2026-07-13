using System.Globalization;
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
    bool HasThreeSetPdf);

/// <summary>Parses BIT's property-detail page (fixture <c>detail_pr001_h05.html</c>).</summary>
public static class DetailParser
{
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
        var caseNo = CaseNumberParser.Parse(doc.DocumentNode.InnerText);

        return new PropertyDetail(saleUnitId, courtId, caseNo, lat, lng, hasPdf);
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
