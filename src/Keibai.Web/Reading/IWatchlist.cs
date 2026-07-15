using Keibai.Core.Domain;
using Keibai.Core.Search;

namespace Keibai.Web.Reading;

/// <summary>
/// Read + write accessor for the personal watchlist and saved searches (<c>/jp/watchlist</c>). Personal
/// use — a single operator, no user id. Writes happen from static-SSR <c>EditForm</c> POSTs (star toggle,
/// save/delete search) using Post-Redirect-Get, so no interactive circuit is needed.
/// </summary>
public interface IWatchlist
{
    /// <summary>The set of starred property ids (for marking rows in a list).</summary>
    Task<IReadOnlySet<string>> WatchedIdsAsync(CancellationToken ct = default);

    /// <summary>Whether <paramref name="propertyItemId"/> is starred.</summary>
    Task<bool> IsWatchedAsync(string propertyItemId, CancellationToken ct = default);

    /// <summary>
    /// Toggle the star on <paramref name="propertyItemId"/>; returns the NEW state (true = now watched).
    /// Adding a star snapshots the property's current archive/status/result state so the nightly digest
    /// reports only subsequent changes, never the state at star time.
    /// </summary>
    Task<bool> ToggleAsync(string propertyItemId, CancellationToken ct = default);

    /// <summary>The starred properties (that still exist), newest-starred first.</summary>
    Task<IReadOnlyList<PropertyItem>> WatchedPropertiesAsync(CancellationToken ct = default);

    /// <summary>All saved searches, newest first.</summary>
    Task<IReadOnlyList<SavedSearch>> SavedSearchesAsync(CancellationToken ct = default);

    /// <summary>Persist a named saved search; returns the stored document.</summary>
    Task<SavedSearch> SaveSearchAsync(string name, PropertyQuery query, CancellationToken ct = default);

    /// <summary>Delete a saved search by id.</summary>
    Task DeleteSavedSearchAsync(string id, CancellationToken ct = default);
}
