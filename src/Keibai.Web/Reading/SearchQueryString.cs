using System.Globalization;
using Keibai.Core.Domain;
using Keibai.Core.Search;
using Microsoft.AspNetCore.Http;

namespace Keibai.Web.Reading;

/// <summary>
/// Maps a <see cref="PropertyQuery"/> to/from the URL query string. The search page is static SSR — its
/// filter form is a plain <c>GET</c> form, so every search is a bookmarkable URL and paging is just
/// <c>?page=N</c> (mirrors the offmarket.deals paging convention). Canonical param names are the public
/// contract the filter form's <c>name=</c> attributes must use.
/// </summary>
public static class SearchQueryString
{
    /// <summary>Parse a <see cref="PropertyQuery"/> from the request query collection.</summary>
    public static PropertyQuery Parse(IQueryCollection q) => new()
    {
        PrefectureId = Str(q["pref"]),
        CourtId = Str(q["court"]),
        Type = Enum.TryParse<SaleCls>(q["type"], out var t) ? t : null,
        MinPrice = Long(q["min"]),
        MaxPrice = Long(q["max"]),
        Status = Enum.TryParse<BiddingStatus>(q["status"], true, out var s) ? s : BiddingStatus.Any,
        OpeningFrom = Date(q["from"]),
        OpeningTo = Date(q["to"]),
        Text = Str(q["q"]),
        HasDocuments = Bool(q["docs"]),
        Sort = Enum.TryParse<PropertySort>(q["sort"], true, out var so) ? so : PropertySort.DeadlineAsc,
        Page = Page(q["page"]),
        PageSize = 25,
    };

    /// <summary>Build a query string (with leading <c>?</c>) for <paramref name="query"/>, optionally overriding the page.</summary>
    public static string ToQueryString(PropertyQuery query, int? page = null)
    {
        var parts = new List<string>();
        Add(parts, "pref", query.PrefectureId);
        Add(parts, "court", query.CourtId);
        Add(parts, "type", query.Type?.ToString());
        Add(parts, "min", query.MinPrice?.ToString(CultureInfo.InvariantCulture));
        Add(parts, "max", query.MaxPrice?.ToString(CultureInfo.InvariantCulture));
        Add(parts, "status", query.Status == BiddingStatus.Any ? null : query.Status.ToString());
        Add(parts, "from", query.OpeningFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "to", query.OpeningTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "q", query.Text);
        Add(parts, "docs", query.HasDocuments ? "1" : null);
        Add(parts, "sort", query.Sort == PropertySort.DeadlineAsc ? null : query.Sort.ToString());
        var effectivePage = page ?? query.Page;
        Add(parts, "page", effectivePage > 1 ? effectivePage.ToString(CultureInfo.InvariantCulture) : null);
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static void Add(List<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={Uri.EscapeDataString(value)}");
        }
    }

    private static string? Str(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool Bool(string? v) => v is "1" or "true" or "on";

    private static long? Long(string? v) =>
        long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DateOnly? Date(string? v) =>
        DateOnly.TryParse(v, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int Page(string? v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 1 ? p : 1;
}
