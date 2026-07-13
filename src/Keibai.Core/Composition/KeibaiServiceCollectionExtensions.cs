using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Storage;
using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Keibai.Core.Composition;

/// <summary>
/// THE merge artifact. The standalone host and (later) the OMD host both call
/// <see cref="AddKeibai"/> to register Keibai's ancillary Marten store, blob store, BIT client and
/// options — byte-identical before and after the merge.
/// </summary>
public static class KeibaiServiceCollectionExtensions
{
    /// <summary>
    /// Register everything Keibai needs beside a host: the <c>keibai</c>-schema ancillary Marten store,
    /// the filesystem blob store, the rate-limited BIT <see cref="HttpClient"/>, and options bound from
    /// the <c>Keibai:</c> config section.
    /// </summary>
    public static IServiceCollection AddKeibai(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BitOptions>(configuration.GetSection(BitOptions.SectionName));

        var connectionString = configuration.GetConnectionString("Keibai")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Keibai is required (the keibai Marten database).");

        // ANCILLARY store — never AddMarten. schema 'keibai' keeps it isolated from the OMD default store.
        services.AddMartenStore<IKeibaiStore>((StoreOptions opts) =>
            {
                opts.Connection(connectionString);
                // Schema is 'keibai' in every real deployment; tests override it (Keibai:SchemaName) to an
                // ephemeral per-run schema so a shared database gives each test host full isolation.
                var schema = configuration["Keibai:SchemaName"];
                opts.DatabaseSchemaName = string.IsNullOrWhiteSpace(schema) ? "keibai" : schema;
                opts.UseSystemTextJsonForSerialization();
                opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

                opts.Schema.For<Court>().Identity(x => x.Id);
                opts.Schema.For<PropertyItem>()
                    .Identity(x => x.Id)
                    .Index(x => x.CourtId)
                    .Index(x => x.PrefectureId);
                opts.Schema.For<ArchivedDocument>().Identity(x => x.Id).Index(x => x.PropertyItemId);
                opts.Schema.For<SaleResult>().Index(x => x.PropertyItemId);
                opts.Schema.For<CrawlRun>().Index(x => x.CourtId!).Index(x => x.PrefectureId!);
                opts.Schema.For<RawCapture>().Index(x => x.ContentHash);
            })
            .ApplyAllDatabaseChangesOnStartup();

        services.AddSingleton<IKeibaiStoreAccessor, KeibaiStoreAccessor>();

        // Blob store: local filesystem content-addressed store under Keibai:BlobStore:Root. Treat a blank
        // config value (the appsettings.json default) as "unset" and fall back to a path next to the app.
        var configuredRoot = configuration["Keibai:BlobStore:Root"];
        var blobRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppContext.BaseDirectory, "blobstore")
            : configuredRoot;
        services.AddSingleton<IDocumentBlobStore>(new FileSystemBlobStore(blobRoot));

        // Rate limiter is a singleton — ONE global gate for all BIT traffic.
        services.AddSingleton(sp => new BitRateLimiter(
            sp.GetRequiredService<IOptionsMonitor<BitOptions>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddTransient<BitRateLimitingHandler>();

        // The BIT HttpClient: honest UA + base address, Polly exponential backoff OUTSIDE the
        // rate-limit/kill-switch handler — registration order puts Polly outermost, so every retry
        // re-enters the limiter and is itself spaced ≥ MinRequestInterval (and re-checks the
        // kill-switch). HttpClient.Timeout is infinite: the real per-attempt timeout lives in
        // BitRateLimitingHandler, measured after the rate-limit slot is acquired, so time queued
        // behind the global gate can never cancel a request (see BitOptions.RequestTimeout).
        services.AddHttpClient<BitClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<BitOptions>>().Value;
                http.BaseAddress = new Uri(options.BaseUrl);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddPolicyHandler((sp, _) => BitRetryPolicy.Create(sp))
            .AddHttpMessageHandler<BitRateLimitingHandler>();

        return services;
    }
}
