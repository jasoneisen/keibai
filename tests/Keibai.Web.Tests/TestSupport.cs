using Keibai.Core.Domain;
using Keibai.Core.Search;
using Keibai.Web.Reading;
using Microsoft.AspNetCore.Http;

namespace Keibai.Web.Tests;

/// <summary>Builds a minimal cascading <see cref="HttpContext"/> for static-SSR component tests.</summary>
public static class TestHttp
{
    /// <summary>A GET request context with an optional query string (with or without a leading <c>?</c>).</summary>
    public static HttpContext Get(string? queryString = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        if (!string.IsNullOrEmpty(queryString))
        {
            http.Request.QueryString = new QueryString(
                queryString.StartsWith('?') ? queryString : "?" + queryString);
        }

        return http;
    }
}

/// <summary>Scriptable <see cref="IPropertyReader"/> for component tests — set the properties, read the captures.</summary>
public sealed class FakePropertyReader : IPropertyReader
{
    /// <summary>What <see cref="SearchAsync"/> returns.</summary>
    public PagedResult<PropertyItem> SearchResult { get; set; } = PagedResult<PropertyItem>.Empty(1, 25);
    /// <summary>What <see cref="GetDetailAsync"/> returns (null → 404).</summary>
    public PropertyDetailView? Detail { get; set; }
    /// <summary>What <see cref="GetFacetsAsync"/> returns.</summary>
    public SearchFacets Facets { get; set; } = new([], []);
    /// <summary>What <see cref="GetPdfAsync"/> returns.</summary>
    public ArchivedPdf? Pdf { get; set; }
    /// <summary>What <see cref="GetMapPinsAsync"/> returns.</summary>
    public MapPinsResult MapPins { get; set; } = new([], 0, 0, false);

    /// <summary>The last query passed to <see cref="SearchAsync"/>.</summary>
    public PropertyQuery? LastQuery { get; private set; }
    /// <summary>The last (courtId, saleUnitId) passed to <see cref="GetDetailAsync"/>.</summary>
    public (string Court, string Unit)? LastDetail { get; private set; }

    /// <inheritdoc/>
    public Task<PagedResult<PropertyItem>> SearchAsync(PropertyQuery query, CancellationToken ct = default)
    {
        LastQuery = query;
        return Task.FromResult(SearchResult);
    }

    /// <inheritdoc/>
    public Task<PropertyDetailView?> GetDetailAsync(string courtId, string saleUnitId, CancellationToken ct = default)
    {
        LastDetail = (courtId, saleUnitId);
        return Task.FromResult(Detail);
    }

    /// <inheritdoc/>
    public Task<SearchFacets> GetFacetsAsync(CancellationToken ct = default) => Task.FromResult(Facets);

    /// <inheritdoc/>
    public Task<ArchivedPdf?> GetPdfAsync(string propertyItemId, string sha256, CancellationToken ct = default) =>
        Task.FromResult(Pdf);

    /// <inheritdoc/>
    public Task<MapPinsResult> GetMapPinsAsync(PropertyQuery query, CancellationToken ct = default)
    {
        LastQuery = query;
        return Task.FromResult(MapPins);
    }
}

/// <summary>Scriptable <see cref="IResultsReader"/> for component tests.</summary>
public sealed class FakeResultsReader : IResultsReader
{
    /// <summary>What <see cref="SearchAsync"/> returns.</summary>
    public PagedResult<SaleResultView> SearchResult { get; set; } = PagedResult<SaleResultView>.Empty(1, 50);
    /// <summary>What <see cref="PrefectureStatsAsync"/> returns.</summary>
    public IReadOnlyList<PrefectureResultStats> Stats { get; set; } = [];
    /// <summary>What <see cref="GetFacetsAsync"/> returns.</summary>
    public ResultsFacets Facets { get; set; } = new([], [], []);
    /// <summary>The last query passed to <see cref="SearchAsync"/>.</summary>
    public ResultsQuery? LastQuery { get; private set; }

    /// <inheritdoc/>
    public Task<PagedResult<SaleResultView>> SearchAsync(ResultsQuery query, CancellationToken ct = default)
    {
        LastQuery = query;
        return Task.FromResult(SearchResult);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PrefectureResultStats>> PrefectureStatsAsync(CancellationToken ct = default) =>
        Task.FromResult(Stats);

    /// <inheritdoc/>
    public Task<ResultsFacets> GetFacetsAsync(CancellationToken ct = default) => Task.FromResult(Facets);
}

/// <summary>Scriptable <see cref="IOpsReader"/> for component tests.</summary>
public sealed class FakeOpsReader : IOpsReader
{
    /// <summary>What <see cref="GetAsync"/> returns.</summary>
    public OpsSnapshot Snapshot { get; set; } = new(
        0, 0, 50, 0, [], [], new DailyStatsView("2026-07-15", 0, 0, 0, 0, 0), null, []);

    /// <inheritdoc/>
    public Task<OpsSnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);
}

/// <summary>Scriptable <see cref="IWatchlist"/> for component tests.</summary>
public sealed class FakeWatchlist : IWatchlist
{
    /// <summary>Starred ids.</summary>
    public HashSet<string> Watched { get; } = new(StringComparer.Ordinal);
    /// <summary>What <see cref="WatchedPropertiesAsync"/> returns.</summary>
    public IReadOnlyList<PropertyItem> WatchedProperties { get; set; } = [];
    /// <summary>What <see cref="SavedSearchesAsync"/> returns.</summary>
    public IReadOnlyList<SavedSearch> Saved { get; set; } = [];

    /// <inheritdoc/>
    public Task<IReadOnlySet<string>> WatchedIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(Watched);

    /// <inheritdoc/>
    public Task<bool> IsWatchedAsync(string propertyItemId, CancellationToken ct = default) =>
        Task.FromResult(Watched.Contains(propertyItemId));

    /// <inheritdoc/>
    public Task<bool> ToggleAsync(string propertyItemId, CancellationToken ct = default)
    {
        if (!Watched.Remove(propertyItemId))
        {
            Watched.Add(propertyItemId);
        }

        return Task.FromResult(Watched.Contains(propertyItemId));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PropertyItem>> WatchedPropertiesAsync(CancellationToken ct = default) =>
        Task.FromResult(WatchedProperties);

    /// <inheritdoc/>
    public Task<IReadOnlyList<SavedSearch>> SavedSearchesAsync(CancellationToken ct = default) =>
        Task.FromResult(Saved);

    /// <inheritdoc/>
    public Task<SavedSearch> SaveSearchAsync(string name, PropertyQuery query, CancellationToken ct = default) =>
        Task.FromResult(new SavedSearch { Id = "x", Name = name, Query = query });

    /// <inheritdoc/>
    public Task DeleteSavedSearchAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
}
