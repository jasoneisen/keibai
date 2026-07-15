using Keibai.Core.Alerting;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Search;
using Marten;
using Microsoft.Extensions.Logging;

namespace Keibai.Core.Ingestion;

/// <summary>
/// The Phase 3 nightly saved-search + watchlist digest. Non-BIT (it reads the store only), so it does NOT
/// ride the sequential <c>keibai-ingestion</c> queue. Sends ONE alert — never per-item spam — summarizing
/// new properties matching each <see cref="SavedSearch"/> since its last run and status changes on watched
/// properties (3点セット archived / bidding advanced / 売却結果 published). "No news is good news": it
/// sends nothing when nothing changed. Idempotent — it advances each search's baseline and each watch's
/// last-notified snapshot, so a same-night re-run reports nothing new.
/// </summary>
public static class SavedSearchDigestHandler
{
    private const int MaxIdsPerSearch = 10;

    /// <summary>Evaluate all saved searches + watched properties and send the single digest alert.</summary>
    public static async Task Handle(
        SendSavedSearchDigest _,
        IKeibaiStoreAccessor store,
        IAlerter alerter,
        TimeProvider time,
        ILogger<SavedSearchDigestMarker> log,
        CancellationToken ct)
    {
        var today = JstClock.Today(time);
        var now = time.GetUtcNow();

        await using var session = store.LightweightSession();
        var searches = await session.Query<SavedSearch>().ToListAsync(ct).ConfigureAwait(false);
        var watches = await session.Query<WatchlistEntry>().ToListAsync(ct).ConfigureAwait(false);

        var lines = new List<string>();
        var newMatches = 0;

        foreach (var search in searches.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var matched = (await PropertySearch
                    .Apply(session.Query<PropertyItem>(), search.Query, today)
                    .Select(x => x.Id)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToList();

            var known = search.LastMatchIds.ToHashSet(StringComparer.Ordinal);
            var fresh = matched.Where(id => !known.Contains(id)).ToList();

            // The first run only establishes the baseline — never dump every pre-existing match as "new".
            if (search.LastRunAt is not null && fresh.Count > 0)
            {
                newMatches += fresh.Count;
                lines.Add($"🔎 \"{search.Name}\": {fresh.Count} new match(es) (of {matched.Count} total).");
                foreach (var id in fresh.Take(MaxIdsPerSearch))
                {
                    lines.Add("   • /jp/property/" + id.Replace(":", "/", StringComparison.Ordinal));
                }

                if (fresh.Count > MaxIdsPerSearch)
                {
                    lines.Add($"   • …and {fresh.Count - MaxIdsPerSearch} more.");
                }
            }

            search.LastMatchIds = matched;
            search.LastRunAt = now;
            session.Store(search);
        }

        var watchChanges = 0;
        foreach (var entry in watches)
        {
            var item = await session.LoadAsync<PropertyItem>(entry.Id, ct).ConfigureAwait(false);
            if (item is null)
            {
                continue;
            }

            var archived = item.LastArchivedAt is not null;
            var status = PropertySearch.DeriveStatus(item, today);
            var hasResult = await session.Query<SaleResult>()
                .Where(r => r.PropertyItemId == entry.Id)
                .AnyAsync(ct)
                .ConfigureAwait(false);

            var changes = new List<string>();
            if (archived && !entry.LastNotifiedArchived)
            {
                changes.Add("3点セット archived");
            }

            if (entry.LastNotifiedStatus is not null && status != entry.LastNotifiedStatus)
            {
                changes.Add($"status {entry.LastNotifiedStatus} → {status}");
            }

            if (hasResult && !entry.LastNotifiedHasResult)
            {
                changes.Add("売却結果 published");
            }

            if (changes.Count > 0)
            {
                watchChanges++;
                var label = item.Case?.Raw ?? item.SaleUnitId;
                lines.Add($"⭐ {label}: {string.Join(", ", changes)} "
                    + $"— /jp/property/{item.CourtId}/{item.SaleUnitId}");
            }

            entry.LastNotifiedArchived = archived;
            entry.LastNotifiedStatus = status;
            entry.LastNotifiedHasResult = hasResult;
            session.Store(entry);
        }

        await session.SaveChangesAsync(ct).ConfigureAwait(false);

        if (lines.Count == 0)
        {
            log.LogInformation(
                "Digest: nothing new ({Searches} saved searches, {Watches} watched).",
                searches.Count, watches.Count);
            return; // no news is good news
        }

        await alerter.SendAsync(
            new Alert(
                $"Keibai digest — {newMatches} new match(es), {watchChanges} watch update(s)",
                string.Join("\n", lines),
                AlertSeverity.Info),
            ct).ConfigureAwait(false);

        log.LogInformation(
            "Digest sent: {New} new matches across {Searches} searches, {Changes} watch updates.",
            newMatches, searches.Count, watchChanges);
    }
}

/// <summary>Marker for typed <c>ILogger</c> injection in the digest handler.</summary>
public sealed class SavedSearchDigestMarker;
