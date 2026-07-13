namespace Keibai.Core.Ingestion;

/// <summary>The 47 JIS prefecture codes BIT accepts as <c>prefecturesId</c> (01 Hokkaido … 47 Okinawa).</summary>
public static class Prefectures
{
    /// <summary>All 47 zero-padded prefecture codes.</summary>
    public static readonly IReadOnlyList<string> All =
        Enumerable.Range(1, 47).Select(i => i.ToString("D2")).ToArray();
}
