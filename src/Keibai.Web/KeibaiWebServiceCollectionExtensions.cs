using Keibai.Web.Reading;
using Microsoft.Extensions.DependencyInjection;

namespace Keibai.Web;

/// <summary>
/// Registers the Phase 3 read-side services the Blazor pages consume. Called by the standalone host
/// beside <c>AddKeibai</c>; at merge time the offmarket.deals host calls it too (the readers depend only on
/// the ancillary <c>IKeibaiStore</c> + blob store that <c>AddKeibai</c> registers, so this is the whole
/// Web-side merge artifact). Readers are scoped — one per request/render.
/// </summary>
public static class KeibaiWebServiceCollectionExtensions
{
    /// <summary>Register the property / results / ops readers and the watchlist store.</summary>
    public static IServiceCollection AddKeibaiWeb(this IServiceCollection services)
    {
        services.AddScoped<IPropertyReader, PropertyReader>();
        services.AddScoped<IResultsReader, ResultsReader>();
        services.AddScoped<IOpsReader, OpsReader>();
        services.AddScoped<IWatchlist, WatchlistStore>();
        return services;
    }
}
