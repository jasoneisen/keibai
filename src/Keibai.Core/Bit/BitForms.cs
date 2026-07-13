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
    /// Prefecture-level search body for <c>/app/areaselect/ps002/h05</c>. BIT 500s on a partial body, so
    /// this replays the FULL 70-field form captured during recon (all four sale classes, the Spring
    /// <c>_...=on</c> checkbox markers, and the empty detail-condition fields). The region <c>blockCls</c>
    /// + <c>blockName</c> are REQUIRED and looked up from the prefecture (blank block → 500), verified
    /// live against Tokyo (prefecture 13 → block 03 関東, totalCount 42). See <c>docs/bit-api.md</c>.
    /// </summary>
    public static List<KeyValuePair<string, string>> PrefectureSearchPairs(
        string prefectureId, int currentPage, int pageSize)
    {
        var (blockCls, blockName) = Regions.BlockFor(prefectureId);
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("saleCls", "1"),
            new("saleCls", "2"),
            new("saleCls", "3"),
            new("saleCls", "4"),
            new("saleStandardAmountCls", "1"),
            new("saleStandardAmountTextMin", string.Empty),
            new("saleStandardAmountTextMax", string.Empty),
            new("_detailAreaInfoDto.landConditionClsList", "on"),
            new("detailAreaInfoDto.landAreaMin", string.Empty),
            new("detailAreaInfoDto.landAreaMax", string.Empty),
            new("detailAreaInfoDto.detachedFloorAreaMin", string.Empty),
            new("detailAreaInfoDto.detachedFloorAreaMax", string.Empty),
            new("detailAreaInfoDto.detachedStoreyMin", string.Empty),
            new("detailAreaInfoDto.detachedStoreyMax", string.Empty),
            new("_detailAreaInfoDto.detachedPossessorExist", "on"),
            new("_detailAreaInfoDto.detachedPossessorNone", "on"),
            new("_detailAreaInfoDto.detachedDebtorFlg", "on"),
            new("_detailAreaInfoDto.detachedOwnershipFlg", "on"),
            new("_detailAreaInfoDto.detachedSuperficiesFlg", "on"),
            new("_detailAreaInfoDto.detachedLeaseFlg", "on"),
            new("_detailAreaInfoDto.detachedOthersSiteUseFlg", "on"),
            new("detailAreaInfoDto.mansionExclusiveAreaMin", string.Empty),
            new("detailAreaInfoDto.mansionExclusiveAreaMax", string.Empty),
            new("detailAreaInfoDto.mansionStoreyMin", string.Empty),
            new("detailAreaInfoDto.mansionStoreyMax", string.Empty),
            new("detailAreaInfoDto.mansionFloorMin", string.Empty),
            new("detailAreaInfoDto.mansionFloorMax", string.Empty),
            new("_detailAreaInfoDto.mansionPossessorExist", "on"),
            new("_detailAreaInfoDto.mansionPossessorNone", "on"),
            new("_detailAreaInfoDto.mansionDebtorFlg", "on"),
            new("detailAreaInfoDto.mansionAdminExpensesMin", string.Empty),
            new("detailAreaInfoDto.mansionAdminExpensesMax", string.Empty),
            new("_detailAreaInfoDto.mansionOwnershipFlg", "on"),
            new("_detailAreaInfoDto.mansionSuperficiesFlg", "on"),
            new("_detailAreaInfoDto.mansionLeaseFlg", "on"),
            new("_detailAreaInfoDto.mansionOthersSiteUseFlg", "on"),
            new("_detailAreaInfoDto.otherLandConditionClsList", "on"),
            new("detailAreaInfoDto.otherLandAreaMin", string.Empty),
            new("detailAreaInfoDto.otherLandAreaMax", string.Empty),
            new("tabId", "property"),
            new("stationBackPage", string.Empty),
            new("prefecturesId", prefectureId),
            new("blockCls", blockCls),
            new("mapShowFlag", "0"),
            new("searchType", "1"),
            new("municipalityId", string.Empty),
            new("municipalityNm", string.Empty),
            new("mapSelectedAreaName", string.Empty),
            new("detailConditionOpenFlg", "0"),
            new("saleClsList", "1,2,3,4"),
            new("areaIdList", string.Empty),
            new("blockName", blockName),
            new("hasCondition", "0"),
            new("landDetalConditionOpenFlag", string.Empty),
            new("detachedDetalConditionOpenFlag", string.Empty),
            new("mansionDetalConditionOpenFlag", string.Empty),
            new("otherLandDetalConditionOpenFlag", string.Empty),
            new("saleStandardAmountMin", string.Empty),
            new("saleStandardAmountMax", string.Empty),
            new("detailAreaInfoDto.landCls", string.Empty),
            new("detailAreaInfoDto.detachedStructure1", string.Empty),
            new("detailAreaInfoDto.detachedStructure2", string.Empty),
            new("detailAreaInfoDto.detachedRoomArrangement", string.Empty),
            new("detailAreaInfoDto.detacheLandKind", string.Empty),
            new("detailAreaInfoDto.mansionStructure1", string.Empty),
            new("detailAreaInfoDto.mansionStructure2", string.Empty),
            new("detailAreaInfoDto.mansionRoomArrangement", string.Empty),
            new("detailAreaInfoDto.mansionLandKind", string.Empty),
            new("detailAreaInfoDto.mansionFloor", string.Empty),
            new("detailAreaInfoDto.otherLandCls", string.Empty),
            // Paging state (h05 for page 1; /app/search for later pages).
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

    /// <summary>
    /// Paging body for <c>/app/propertyresult/pr001/h39</c>: the previous results page's full
    /// <c>propertyResultForm</c> envelope with the target page + size set (verified live: page 2 returns
    /// a distinct set of sale units, <c>currentPage=2</c>).
    /// </summary>
    public static Dictionary<string, string> ListingPage(string previousPageHtml, int currentPage, int pageSize)
    {
        var form = ExtractForm(previousPageHtml, "propertyResultForm");
        form["currentPage"] = currentPage.ToString(System.Globalization.CultureInfo.InvariantCulture);
        form["pageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        form["pageListChangeFlg"] = "0";
        form["resultListSearchButtonFlag"] = "0";
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
