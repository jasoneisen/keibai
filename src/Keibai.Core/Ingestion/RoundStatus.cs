namespace Keibai.Core.Ingestion;

/// <summary>
/// Derives an <see cref="Domain.AuctionRound"/>'s lifecycle status from its schedule dates relative to
/// today (JST). Pure, so it is unit-testable and shared by the round projection and the UI.
/// </summary>
public static class RoundStatus
{
    /// <summary>Lifecycle: upcoming → viewing (閲覧中) → bidding (入札中) → closed (awaiting 開札) → opened.</summary>
    public static string Derive(
        DateOnly? viewingStart, DateOnly? biddingStart, DateOnly? biddingEnd, DateOnly openingDate, DateOnly today)
    {
        if (today >= openingDate)
        {
            return "opened";
        }

        if (biddingEnd is { } end && today > end)
        {
            return "closed"; // bidding finished, 開札 not yet reached
        }

        if (biddingStart is { } start && today >= start && (biddingEnd is not { } e || today <= e))
        {
            return "bidding";
        }

        if (viewingStart is { } view && today >= view)
        {
            return "viewing";
        }

        return "upcoming";
    }
}
