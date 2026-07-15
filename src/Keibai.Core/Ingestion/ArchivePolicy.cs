using Keibai.Core.Domain;

namespace Keibai.Core.Ingestion;

/// <summary>Eligibility + validation rules for the PDF archiver, factored out so they are unit-testable.</summary>
public static class ArchivePolicy
{
    /// <summary>Minimum plausible 3点セット size — anything smaller is a truncated/garbage response.</summary>
    public const int MinPdfBytes = 1024;

    private static readonly byte[] PdfMagic = "%PDF"u8.ToArray();

    /// <summary>
    /// True when this prefecture's PDFs should be archived. An empty allow-list means "archive nationwide";
    /// otherwise only the listed prefectures are archived (the disk guard).
    /// </summary>
    public static bool PrefectureEligible(string prefectureId, IReadOnlyList<string> archivePrefectures) =>
        archivePrefectures.Count == 0 || archivePrefectures.Contains(prefectureId);

    /// <summary>True when the bytes look like a real, non-trivial PDF (magic bytes + minimum size).</summary>
    public static bool IsProbablyPdf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= MinPdfBytes && bytes.StartsWith(PdfMagic);

    /// <summary>
    /// True when the property is still inside its viewing/bidding window (documents may still exist). Once
    /// 開札 has passed the 3点セット is deleted, so there is nothing left to archive.
    /// </summary>
    public static bool InWindow(PropertyItem item, DateOnly today) =>
        item.OpeningDate is null || item.OpeningDate.Value >= today;

    /// <summary>Order key for deletion priority: soonest 入札期間 end first, unknown deadlines last.</summary>
    public static DateOnly DeadlineKey(PropertyItem item) => item.BiddingEnd ?? DateOnly.MaxValue;

    /// <summary>The full 3点セット download URL for a property (for <see cref="ArchivedDocument.SourceUrl"/>).</summary>
    public static string ThreeSetUrl(string baseUrl, string courtId, string saleUnitId) =>
        $"{baseUrl.TrimEnd('/')}/app/detail/pd001/h04?courtId={courtId}&saleUnitId={saleUnitId}";
}
