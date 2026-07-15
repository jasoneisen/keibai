using Keibai.Core.Ingestion;
using Xunit;

namespace Keibai.Tests;

public class RoundStatusTests
{
    private static readonly DateOnly View = new(2026, 6, 26);
    private static readonly DateOnly BidStart = new(2026, 7, 15);
    private static readonly DateOnly BidEnd = new(2026, 7, 22);
    private static readonly DateOnly Opening = new(2026, 7, 28);

    private static string At(DateOnly today) =>
        RoundStatus.Derive(View, BidStart, BidEnd, Opening, today);

    [Fact]
    public void Lifecycle_transitions_through_the_schedule()
    {
        Assert.Equal("upcoming", At(new DateOnly(2026, 6, 1)));
        Assert.Equal("viewing", At(View));
        Assert.Equal("viewing", At(new DateOnly(2026, 7, 14)));
        Assert.Equal("bidding", At(BidStart));
        Assert.Equal("bidding", At(BidEnd));
        Assert.Equal("closed", At(new DateOnly(2026, 7, 25)));
        Assert.Equal("opened", At(Opening));
        Assert.Equal("opened", At(new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public void Handles_missing_dates_gracefully()
    {
        // Only the 開札 date is known.
        Assert.Equal("upcoming", RoundStatus.Derive(null, null, null, Opening, new DateOnly(2026, 7, 1)));
        Assert.Equal("opened", RoundStatus.Derive(null, null, null, Opening, Opening));
    }
}
