using System.Text;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Xunit;

namespace Keibai.Tests;

public class ArchivePolicyTests
{
    [Fact]
    public void Empty_allowlist_means_archive_everything()
    {
        Assert.True(ArchivePolicy.PrefectureEligible("13", []));
        Assert.True(ArchivePolicy.PrefectureEligible("47", []));
    }

    [Fact]
    public void Non_empty_allowlist_restricts_to_listed_prefectures()
    {
        Assert.True(ArchivePolicy.PrefectureEligible("13", ["13", "14"]));
        Assert.False(ArchivePolicy.PrefectureEligible("27", ["13", "14"]));
    }

    [Fact]
    public void Pdf_validation_requires_magic_bytes_and_minimum_size()
    {
        var good = new byte[ArchivePolicy.MinPdfBytes];
        Encoding.ASCII.GetBytes("%PDF-1.7").CopyTo(good, 0);
        Assert.True(ArchivePolicy.IsProbablyPdf(good));

        Assert.False(ArchivePolicy.IsProbablyPdf(Encoding.ASCII.GetBytes("%PDF")));      // too small
        Assert.False(ArchivePolicy.IsProbablyPdf(new byte[ArchivePolicy.MinPdfBytes])); // no magic bytes
        Assert.False(ArchivePolicy.IsProbablyPdf(Encoding.UTF8.GetBytes("<html>…</html>")));
    }

    [Fact]
    public void InWindow_is_true_until_the_opening_date_passes()
    {
        var today = new DateOnly(2026, 7, 20);
        Assert.True(ArchivePolicy.InWindow(new PropertyItem
        {
            Id = "c:s", SaleUnitId = "s", CourtId = "c", PrefectureId = "13", OpeningDate = new DateOnly(2026, 7, 28),
        }, today));
        Assert.False(ArchivePolicy.InWindow(new PropertyItem
        {
            Id = "c:s", SaleUnitId = "s", CourtId = "c", PrefectureId = "13", OpeningDate = new DateOnly(2026, 7, 19),
        }, today));
        // Unknown opening date → assume still in window (better to try than to miss the archive).
        Assert.True(ArchivePolicy.InWindow(new PropertyItem
        {
            Id = "c:s", SaleUnitId = "s", CourtId = "c", PrefectureId = "13",
        }, today));
    }

    [Fact]
    public void Deadline_priority_orders_soonest_bidding_end_first_with_unknowns_last()
    {
        var items = new[]
        {
            Item("late", new DateOnly(2026, 8, 1)),
            Item("none", null),
            Item("soon", new DateOnly(2026, 7, 16)),
        };

        var ordered = items.OrderBy(ArchivePolicy.DeadlineKey).Select(i => i.SaleUnitId).ToArray();

        Assert.Equal(["soon", "late", "none"], ordered);

        static PropertyItem Item(string id, DateOnly? end) => new()
        {
            Id = $"c:{id}", SaleUnitId = id, CourtId = "c", PrefectureId = "13", BiddingEnd = end,
        };
    }
}
