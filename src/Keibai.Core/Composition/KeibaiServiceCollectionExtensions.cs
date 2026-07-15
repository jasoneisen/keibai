using Keibai.Core.Alerting;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Storage;
using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        services.Configure<Alerting.AlertOptions>(configuration.GetSection(Alerting.AlertOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

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
                    .Index(x => x.PrefectureId)
                    // Archive priority queries order by soonest bidding deadline; results scheduling finds
                    // properties whose 開札 is today — both want an index.
                    .Index(x => x.BiddingEnd!)
                    .Index(x => x.OpeningDate!)
                    // Phase 3 search filters on property type + 売却基準価額 (price range) — index both so a
                    // typical search never full-scans the collection.
                    .Index(x => x.SaleCls!)
                    .Index(x => x.SaleStandardAmount!);
                opts.Schema.For<ArchivedDocument>()
                    .Identity(x => x.Id)
                    .Index(x => x.PropertyItemId)
                    .Index(x => x.Sha256);
                opts.Schema.For<SaleResult>()
                    .Identity(x => x.Id)
                    .Index(x => x.PropertyItemId!)
                    .Index(x => x.CourtId!)
                    .Index(x => x.OpeningDate!);
                opts.Schema.For<AuctionCase>().Identity(x => x.Id).Index(x => x.CourtId);
                opts.Schema.For<AuctionRound>()
                    .Identity(x => x.Id)
                    .Index(x => x.CourtId)
                    .Index(x => x.OpeningDate)
                    .Index(x => x.Status);
                opts.Schema.For<DailyStats>().Identity(x => x.Id);
                opts.Schema.For<CrawlRun>().Index(x => x.CourtId!).Index(x => x.PrefectureId!);
                opts.Schema.For<RawCapture>().Index(x => x.ContentHash);

                // Phase 3 personalization + ops docs.
                opts.Schema.For<SavedSearch>().Identity(x => x.Id);
                opts.Schema.For<WatchlistEntry>().Identity(x => x.Id);
                opts.Schema.For<Alerting.AlertLog>().Index(x => x.At);
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

        AddAlerting(services);
        services.AddSingleton<Monitoring.BitBlockResponder>();

        return services;
    }

    /// <summary>
    /// Register the alerting composite. ntfy uses its OWN named <see cref="HttpClient"/> (never the
    /// rate-limited BIT client). The registered <see cref="IAlerter"/> fans out to the providers listed
    /// in <c>Keibai:Alerts:Providers</c>; <see cref="LoggingAlerter"/> is always included so a healthy
    /// system still leaves a durable local trail, and it is the fallback when no provider is configured.
    /// </summary>
    private static void AddAlerting(IServiceCollection services)
    {
        services.AddHttpClient(NtfyAlerter.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<NtfyAlerter>();
        services.AddSingleton<SmtpAlerter>();
        services.AddSingleton<LoggingAlerter>();
        services.AddSingleton<StoringAlerter>();

        services.AddSingleton<IAlerter>(sp =>
        {
            var providers = sp.GetRequiredService<IOptions<AlertOptions>>().Value.Providers;
            var sinks = new List<IAlerter>();
            foreach (var provider in providers)
            {
                switch (provider.Trim().ToLowerInvariant())
                {
                    case "ntfy":
                        sinks.Add(sp.GetRequiredService<NtfyAlerter>());
                        break;
                    case "smtp":
                        sinks.Add(sp.GetRequiredService<SmtpAlerter>());
                        break;
                    case "log":
                        // Added unconditionally below; ignore explicit duplicates.
                        break;
                }
            }

            // Always keep a log trail, and never let a misconfiguration silently drop alerts.
            sinks.Add(sp.GetRequiredService<LoggingAlerter>());
            // Always persist to the AlertLog so the ops dashboard can show recent alerts.
            sinks.Add(sp.GetRequiredService<StoringAlerter>());
            return new CompositeAlerter(sinks, sp.GetRequiredService<ILogger<CompositeAlerter>>());
        });
    }
}
