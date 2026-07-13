namespace Keibai.Core.Ingestion;

/// <summary>The prefecture search codes a nationwide sweep fans out over.</summary>
public static class Prefectures
{
    /// <summary>
    /// All 50 BIT prefecture search codes. Hokkaidō is NOT JIS 01 on BIT: its search form splits it
    /// into four district-court regions with pseudo-codes 91 (札幌), 92 (函館), 93 (旭川), 94 (釧路),
    /// and <c>prefecturesId=01</c> gets BIT's エラー page back (verified live 2026-07-13). The other
    /// 46 prefectures use their zero-padded JIS codes.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
        new[] { "91", "92", "93", "94" }
            .Concat(Enumerable.Range(2, 46).Select(i => i.ToString("D2")))
            .ToArray();
}
