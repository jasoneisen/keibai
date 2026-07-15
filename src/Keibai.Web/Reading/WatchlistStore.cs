using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Keibai.Core.Search;
using Marten;

namespace Keibai.Web.Reading;

/// <summary>Marten-backed <see cref="IWatchlist"/> (stars + saved searches). Single-operator, no user id.</summary>
public sealed class WatchlistStore(IKeibaiStoreAccessor store, TimeProvider time) : IWatchlist
{
    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> WatchedIdsAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var ids = await session.Query<WatchlistEntry>().Select(w => w.Id).ToListAsync(ct).ConfigureAwait(false);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public async Task<bool> IsWatchedAsync(string propertyItemId, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<WatchlistEntry>()
            .Where(w => w.Id == propertyItemId)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> ToggleAsync(string propertyItemId, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var existing = await session.LoadAsync<WatchlistEntry>(propertyItemId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            session.Delete(existing);
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
            return false;
        }

        // Snapshot the property's current state so the nightly digest reports only subsequent changes.
        var item = await session.LoadAsync<PropertyItem>(propertyItemId, ct).ConfigureAwait(false);
        var hasResult = await session.Query<SaleResult>()
            .Where(r => r.PropertyItemId == propertyItemId)
            .AnyAsync(ct)
            .ConfigureAwait(false);

        session.Store(new WatchlistEntry
        {
            Id = propertyItemId,
            AddedAt = time.GetUtcNow(),
            LastNotifiedArchived = item?.LastArchivedAt is not null,
            LastNotifiedStatus = item is null ? null : PropertySearch.DeriveStatus(item, JstClock.Today(time)),
            LastNotifiedHasResult = hasResult,
        });
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PropertyItem>> WatchedPropertiesAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var entries = await session.Query<WatchlistEntry>()
            .OrderByDescending(w => w.AddedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ids = entries.Select(e => e.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var items = await session.Query<PropertyItem>()
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byId = items.ToDictionary(p => p.Id, StringComparer.Ordinal);

        // Preserve newest-starred-first order; drop entries whose property no longer exists.
        return entries.Where(e => byId.ContainsKey(e.Id)).Select(e => byId[e.Id]).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SavedSearch>> SavedSearchesAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await session.Query<SavedSearch>()
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<SavedSearch> SaveSearchAsync(string name, PropertyQuery query, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var doc = new SavedSearch
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled search" : name.Trim(),
            Query = query,
            CreatedAt = time.GetUtcNow(),
            LastMatchIds = [],
        };
        session.Store(doc);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return doc;
    }

    /// <inheritdoc/>
    public async Task DeleteSavedSearchAsync(string id, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        session.Delete<SavedSearch>(id);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
