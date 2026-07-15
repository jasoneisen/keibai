using Keibai.Core.Search;

namespace Keibai.Core.Domain;

/// <summary>
/// A named, saved property search (personal use — single operator, no user id). The nightly digest
/// replays <see cref="Query"/> and reports the rows that are new since <see cref="LastMatchIds"/>.
/// </summary>
public sealed class SavedSearch
{
    /// <summary>Marten identity (guid string).</summary>
    public required string Id { get; set; }
    /// <summary>Operator-chosen name.</summary>
    public required string Name { get; set; }
    /// <summary>The saved filter (replayed verbatim by the digest and the "run" link).</summary>
    public required PropertyQuery Query { get; set; }
    /// <summary>When the search was saved.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>When the digest last evaluated this search (null = never; the first run only sets a baseline).</summary>
    public DateTimeOffset? LastRunAt { get; set; }
    /// <summary>Property ids that matched at the last digest run — the baseline for "new since".</summary>
    public List<string> LastMatchIds { get; set; } = [];
}

/// <summary>
/// A starred property (watchlist). Identity IS the property id (<c>{CourtId}:{SaleUnitId}</c>), so a star
/// is idempotent. Carries the last-notified state so the nightly digest reports only real <em>changes</em>
/// (documents archived / bidding advanced / result published) rather than re-announcing steady state.
/// </summary>
public sealed class WatchlistEntry
{
    /// <summary><c>{CourtId}:{SaleUnitId}</c> — the watched property id.</summary>
    public required string Id { get; set; }
    /// <summary>When it was starred.</summary>
    public DateTimeOffset AddedAt { get; set; }
    /// <summary>Whether the 3点セット was archived as of the last digest (a change → notify).</summary>
    public bool LastNotifiedArchived { get; set; }
    /// <summary>Bidding-status label as of the last digest (a change → notify).</summary>
    public string? LastNotifiedStatus { get; set; }
    /// <summary>Whether a sale result existed as of the last digest (a change → notify).</summary>
    public bool LastNotifiedHasResult { get; set; }
}
