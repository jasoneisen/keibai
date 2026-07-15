namespace Keibai.Tests;

/// <summary>Locates the BIT fixture files copied next to the test binary.</summary>
public static class Fixtures
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures", "bit");

    /// <summary>Read a fixture file's text.</summary>
    public static string Read(string name) => File.ReadAllText(Path.Combine(Root, name));

    /// <summary>The Tokyo (prefecture 13) result-listing page.</summary>
    public static string TokyoResults() => Read("results_ps002_h05_tokyo.html");

    /// <summary>A property-detail page (土地 / land, single 物件).</summary>
    public static string Detail() => Read("detail_pr001_h05.html");

    /// <summary>A 戸建 detail page — a multi-物件 card (物件1 土地 + 物件2 建物).</summary>
    public static string DetailKodate() => Read("detail_kodate_pr001_h05.html");

    /// <summary>A マンション (区分所有建物) detail page.</summary>
    public static string DetailMansion() => Read("detail_mansion_pr001_h05.html");

    /// <summary>The 売却結果 prefecture page (ps007/h02) — carries fiscalYear/codeCls + the court list.</summary>
    public static string SaleResultPrefPage() => Read("saleresult_pref_ps007_h02_tokyo.html");

    /// <summary>The 売却結果 listing page 1 (ps007/h08) for a Tokyo court.</summary>
    public static string SaleResultsListing() => Read("saleresults_ps007_h08_tokyo.html");

    /// <summary>The 売却結果 listing page 2 (pager resultlist/pr002/h03).</summary>
    public static string SaleResultsPage2() => Read("saleresults_pr002_h03_tokyo_p2.html");
}
