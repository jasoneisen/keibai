using HtmlAgilityPack;

namespace Keibai.Core.Bit;

/// <summary>
/// Builds the POST bodies for BIT's form-driven flow. BIT rejects (HTTP 500) any POST missing fields it
/// expects, so the detail endpoint requires the FULL <c>propertyResultForm</c> carried over from the
/// results page — <see cref="ExtractForm"/> lifts it verbatim and the detail/paging calls replay it with
/// the driving fields overridden. See <c>docs/bit-api.md</c>.
/// </summary>
public static class BitForms
{
    /// <summary>
    /// Prefecture-level search body (proven-minimal, ~70 fields) that drives the first result page at
    /// <c>/app/areaselect/ps002/h05</c>. All four sale classes; no price/area narrowing.
    /// </summary>
    public static List<KeyValuePair<string, string>> PrefectureSearchPairs(
        string prefectureId, int currentPage, int pageSize)
    {
        // saleCls repeats (one per checked type); the rest are single-valued. Order is not significant
        // to BIT, but the four saleCls checkboxes plus the comma list are both required.
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("tabId", "property"),
            new("searchType", "1"),
            new("prefecturesId", prefectureId),
            new("blockCls", string.Empty),
            new("saleCls", "1"),
            new("saleCls", "2"),
            new("saleCls", "3"),
            new("saleCls", "4"),
            new("saleClsList", "1,2,3,4"),
            new("saleStandardAmountCls", "1"),
            new("mapShowFlag", "0"),
            new("detailConditionOpenFlg", "0"),
            new("hasCondition", "0"),
            new("municipalityId", string.Empty),
            new("areaIdList", string.Empty),
            new("currentPage", currentPage.ToString()),
            new("pageSize", pageSize.ToString()),
        };
        return pairs;
    }

    /// <summary>
    /// Detail body = the results page's full <c>propertyResultForm</c> with the driving fields set. The
    /// caller passes the raw results HTML from which the form is extracted.
    /// </summary>
    public static Dictionary<string, string> PropertyDetail(
        string resultsPageHtml, string courtId, string saleUnitId)
    {
        var form = ExtractForm(resultsPageHtml, "propertyResultForm");
        form["saleUnitId"] = saleUnitId;
        form["detailCourtId"] = courtId;
        form["transitionTabId"] = "1";
        return form;
    }

    /// <summary>Lift every named input's value from a form by id into a flat dictionary (last wins).</summary>
    public static Dictionary<string, string> ExtractForm(string html, string formId)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var form = doc.DocumentNode.SelectSingleNode($"//form[@id='{formId}']")
                   ?? throw new InvalidOperationException($"Form '{formId}' not found in HTML.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = form.SelectNodes(".//input") ?? new HtmlNodeCollection(form);
        foreach (var input in inputs)
        {
            var name = input.GetAttributeValue<string?>("name", null);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var type = input.GetAttributeValue("type", "text");
            if (type is "checkbox" or "radio" && !input.Attributes.Contains("checked"))
            {
                continue;
            }

            result[name] = HtmlEntity.DeEntitize(input.GetAttributeValue("value", string.Empty));
        }

        var selects = form.SelectNodes(".//select");
        if (selects is not null)
        {
            foreach (var select in selects)
            {
                var name = select.GetAttributeValue<string?>("name", null);
                if (!string.IsNullOrEmpty(name) && !result.ContainsKey(name))
                {
                    result[name] = string.Empty;
                }
            }
        }

        return result;
    }
}
