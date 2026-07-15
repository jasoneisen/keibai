using HtmlAgilityPack;

namespace Keibai.Core.Bit;

/// <summary>Court context extracted from the 売却結果 prefecture page (<c>ps007/h02</c>).</summary>
/// <param name="FiscalYear">Opaque <c>fiscalYear</c> the search requires (echoed into h08).</param>
/// <param name="CodeCls">Opaque <c>codeCls</c> the search requires.</param>
/// <param name="CourtIds">Court codes available in the prefecture (from the <c>peroidCourtId</c> radios).</param>
public sealed record ResultCourtContext(string FiscalYear, string CodeCls, IReadOnlyList<string> CourtIds);

/// <summary>
/// POST bodies for BIT's 売却結果 (sale-results) flow — a separate module from the property search. The
/// flow (see <c>docs/bit-api.md</c>): <c>peroidsearch/ps007/h02</c> (select prefecture → yields
/// <c>fiscalYear</c>/<c>codeCls</c> + the court list) → <c>peroidsearch/ps007/h08</c> (search one court,
/// EMPTY <c>saleScdId</c> = the court's full retained history) → pager <c>resultlist/pr002/h03</c>
/// (repost the results page's <c>resultDetailForm</c> with <c>currPage</c>). h08 is stateless given the
/// right fields; no cookies/AJAX (<c>h04</c>) required.
/// </summary>
public static class ResultForms
{
    /// <summary>Body for the prefecture-select step <c>ps007/h02</c>.</summary>
    public static List<KeyValuePair<string, string>> PrefecturePairs(string prefectureId)
    {
        var (blockCls, blockName) = Regions.BlockFor(prefectureId);
        return
        [
            new("peroidSaleCls", "1"), new("peroidSaleCls", "2"),
            new("peroidSaleCls", "3"), new("peroidSaleCls", "4"),
            new("saleClsList", "1,2,3,4"),
            new("saleType", "1"),
            new("blockCls", blockCls),
            new("blockName", blockName),
            new("prefecturesId", prefectureId),
            new("mapSelectedAreaName", string.Empty),
            new("tabId", "result"),
        ];
    }

    /// <summary>
    /// Body for the by-court results search <c>ps007/h08</c>, page 1. An EMPTY <c>saleScdId</c> returns the
    /// court's full retained history (≈3 years); a specific schedule id would scope to a single round.
    /// </summary>
    public static List<KeyValuePair<string, string>> CourtSearchPairs(
        string prefectureId, string courtId, string fiscalYear, string codeCls, int pageSize)
    {
        var (blockCls, blockName) = Regions.BlockFor(prefectureId);
        return
        [
            new("peroidCourtId", courtId),
            new("peroidSaleCls", "1"), new("peroidSaleCls", "2"),
            new("peroidSaleCls", "3"), new("peroidSaleCls", "4"),
            new("saleScdId", string.Empty),
            new("fiscalYear", fiscalYear),
            new("codeCls", codeCls),
            new("saleClsList", "1,2,3,4"),
            new("courtId", courtId),
            new("saleType", "1"),
            new("blockCls", blockCls),
            new("blockName", blockName),
            new("prefecturesId", prefectureId),
            new("mapShowFlag", "1"),
            new("mapSelectedAreaName", string.Empty),
            new("searchType", "0"),
            new("tabId", "result"),
            new("currentPage", "1"),
            new("pageSize", pageSize.ToString()),
        ];
    }

    /// <summary>
    /// Pager body for <c>resultlist/pr002/h03</c>: replay the results page's full <c>resultDetailForm</c>
    /// envelope (carries court/fiscalYear/codeCls/totalCount) with <c>currPage</c> set (verified: page 2
    /// returns a distinct set of results).
    /// </summary>
    public static Dictionary<string, string> PagerForm(string previousResultsHtml, int page, int pageSize)
    {
        var form = BitForms.ExtractForm(previousResultsHtml, "resultDetailForm");
        form["currPage"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        form["pageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        form["saleClsList"] = "1,2,3,4";
        return form;
    }

    /// <summary>Extract <c>fiscalYear</c>, <c>codeCls</c>, and the court list from the h02 prefecture page.</summary>
    public static ResultCourtContext ParseCourtContext(string h02Html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(h02Html);

        var fiscalYear = HiddenById(doc, "pFiscalYear") ?? string.Empty;
        var codeCls = HiddenById(doc, "pCodeCls") ?? string.Empty;

        var courts = new List<string>();
        var radios = doc.DocumentNode.SelectNodes("//input[@name='peroidCourtId']");
        if (radios is not null)
        {
            foreach (var radio in radios)
            {
                var v = radio.GetAttributeValue<string?>("value", null);
                if (!string.IsNullOrWhiteSpace(v) && !courts.Contains(v))
                {
                    courts.Add(v);
                }
            }
        }

        return new ResultCourtContext(fiscalYear, codeCls, courts);
    }

    private static string? HiddenById(HtmlDocument doc, string id)
    {
        var v = doc.DocumentNode.SelectSingleNode($"//input[@id='{id}']")?.GetAttributeValue<string?>("value", null);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
