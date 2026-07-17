using JasperFx.CodeGeneration.Model;
using Keibai.Core.Bit;
using Keibai.Core.Ingestion;
using Wolverine;
using Wolverine.ErrorHandling;

namespace Keibai.Core.Composition;

/// <summary>
/// The messaging half of the merge artifact. The standalone host and (later) the OMD host both call
/// <see cref="ConfigureKeibaiMessaging"/> on their single <see cref="WolverineOptions"/> so Keibai's
/// handlers are discovered and its local queues are durable and prefixed <c>keibai-</c>.
/// </summary>
public static class KeibaiMessagingExtensions
{
    /// <summary>
    /// Register Keibai's handler assembly and durable-local-queue policy. At merge time the OMD host
    /// adds exactly this call to its existing <c>UseWolverine</c> block.
    /// </summary>
    public static void ConfigureKeibaiMessaging(this WolverineOptions opts)
    {
        // Discover the handlers that live in Keibai.Core via the marker type.
        opts.Discovery.IncludeAssembly(typeof(KeibaiMarker).Assembly);

        // The BIT client is a typed HttpClient (AddHttpClient<BitClient>) — an opaque factory that
        // Wolverine's codegen can't inline-construct, so it must be resolved by service location. Permit
        // it (with a warning) rather than force-registering the client as a plain type, which would lose
        // the IHttpClientFactory handler pipeline (the rate-limit/kill-switch delegating handler).
        opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;

        // All Keibai background work is durable so a restart never loses an in-flight sweep/detail item.
        opts.Policies.UseDurableLocalQueues();

        // ONE strictly sequential queue for every message that touches BIT. Conventional routing gave
        // each message type its own queue, so several handlers executed concurrently and all blocked on
        // the single 1-req/3s rate-limit slot — stretching each other's wall-clock until Wolverine's
        // execution timeout cancelled them mid-handler. Sequential processing means exactly one
        // BIT-touching handler runs at a time (the crawling rules' "single-threaded" made literal);
        // since the rate limiter is the throughput bottleneck anyway, this costs nothing.
        opts.PublishMessage<SyncCourts>().ToLocalQueue("keibai-ingestion");
        opts.PublishMessage<SyncPrefectureListings>().ToLocalQueue("keibai-ingestion");
        opts.PublishMessage<SyncPropertyDetail>().ToLocalQueue("keibai-ingestion");
        // Phase 2 BIT-touching work joins the SAME sequential queue — a new per-type queue would
        // reintroduce the concurrency-vs-rate-limiter deadlock that broke the first sweep. Archives,
        // re-checks, results sync/backfill, and the schedule reconciliations all serialise here.
        opts.PublishMessage<ArchiveDocuments>().ToLocalQueue("keibai-ingestion");
        opts.PublishMessage<RecheckDocuments>().ToLocalQueue("keibai-ingestion");
        opts.PublishMessage<ScheduleArchiveWork>().ToLocalQueue("keibai-ingestion");
        opts.PublishMessage<SyncRoundResults>().ToLocalQueue("keibai-ingestion");
        opts.PublishMessage<BackfillResults>().ToLocalQueue("keibai-ingestion");
        opts.LocalQueue("keibai-ingestion").Sequential();

        // A message that reaches the BIT client while the kill-switch is off throws
        // IngestionDisabledException. The scheduler already skips enqueuing BIT work when ingestion is
        // disabled, so anything that still trips the guard (a durable envelope replayed from a
        // crawl-era run, a manual /sync trigger) is work nobody wants: discard it quietly instead of
        // dead-lettering — a disabled kill-switch once filled the DLQ nightly and, worse, left durable
        // sweep messages armed to fire the moment ingestion was re-enabled.
        opts.OnException<IngestionDisabledException>().Discard();

        // A prefecture-listings handler legitimately runs for minutes (pages × 3s); the 60s default
        // execution timeout cancelled it mid-pagination. NOTE: this is a GLOBAL WolverineOptions
        // setting — at merge time, reconcile with the OMD host (its handlers are all short, so a
        // generous ceiling is safe, but the decision should be conscious).
        opts.DefaultExecutionTimeout = TimeSpan.FromMinutes(30);
    }
}
