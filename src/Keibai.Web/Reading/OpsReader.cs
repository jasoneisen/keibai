using Keibai.Core.Alerting;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keibai.Web.Reading;

/// <summary>Marten + blob-store backed <see cref="IOpsReader"/> for the ops dashboard.</summary>
public sealed class OpsReader(
    IKeibaiStoreAccessor store,
    IDocumentBlobStore blobs,
    IOptions<StorageOptions> storage,
    TimeProvider time,
    ILogger<OpsReader> log) : IOpsReader
{
    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    /// <inheritdoc/>
    public async Task<OpsSnapshot> GetAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();

        var courts = await session.Query<Court>().ToListAsync(ct).ConfigureAwait(false);
        var disabled = courts
            .Where(c => c.CrawlDisabled)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new DisabledCourt(c.Id, c.Name, c.CrawlDisabledReason, c.CrawlDisabledAt))
            .ToList();

        var runs = await session.Query<CrawlRun>()
            .Where(r => r.PrefectureId != null && r.FinishedAt != null)
            .OrderByDescending(r => r.StartedAt)
            .Take(1000)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var prefectures = BuildHealth(runs, time);

        var storageBytes = await blobs.GetTotalBytesAsync(ct).ConfigureAwait(false);

        var todayKey = JstClock.TodayKey(time);
        var stats = await session.LoadAsync<DailyStats>(todayKey, ct).ConfigureAwait(false);
        var today = new DailyStatsView(
            todayKey,
            stats?.ArchiveAttempts ?? 0,
            stats?.PdfsArchived ?? 0,
            stats?.ArchiveFailures ?? 0,
            stats?.SaleResultsUpserted ?? 0,
            stats?.RechecksPerformed ?? 0);

        var recentAlerts = (await session.Query<AlertLog>()
                .OrderByDescending(a => a.At)
                .Take(20)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .Select(a => new AlertRow(a.Title, a.Body, a.Severity, a.At))
            .ToList();

        var queueDepth = await TryQueueDepthAsync(session, ct).ConfigureAwait(false);

        return new OpsSnapshot(
            storageBytes,
            storageBytes / (double)BytesPerGigabyte,
            storage.Value.MaxGigabytes,
            courts.Count,
            disabled,
            prefectures,
            today,
            queueDepth,
            recentAlerts);
    }

    private static IReadOnlyList<PrefectureHealth> BuildHealth(
        IReadOnlyList<CrawlRun> runsNewestFirst, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var list = new List<PrefectureHealth>();

        foreach (var group in runsNewestFirst.GroupBy(r => r.PrefectureId!))
        {
            var ordered = group.OrderByDescending(r => r.StartedAt).ToList();
            var latest = ordered[0];
            var blocked = latest.Notes.Any(n => n.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase));
            var stale = now - latest.StartedAt > TimeSpan.FromHours(36);

            var health = blocked || latest.ItemsFound == 0
                ? "red"
                : latest.Errors > 0 || stale ? "amber" : "green";

            // Oldest→newest ItemsFound for a left-to-right sparkline.
            var history = ordered.Take(12).Select(r => r.ItemsFound).Reverse().ToList();

            list.Add(new PrefectureHealth(
                group.Key,
                PrefectureNames.Of(group.Key),
                latest.StartedAt,
                latest.ItemsFound,
                latest.ItemsNew,
                latest.Errors,
                blocked,
                health,
                history));
        }

        return list.OrderBy(p => p.PrefectureId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Best-effort pending-durable-envelope count from the Wolverine message store. Null when durability is
    /// not wired (e.g. the Testing environment) or the schema is absent — a dashboard nicety, never a hard
    /// dependency.
    /// </summary>
    private async Task<long?> TryQueueDepthAsync(IQuerySession session, CancellationToken ct)
    {
        try
        {
            var counts = await session
                .QueryAsync<long>("select count(*) from keibai_wolverine.wolverine_incoming_envelopes", ct)
                .ConfigureAwait(false);
            return counts.Count > 0 ? counts[0] : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Queue depth unavailable (durability not wired or schema absent).");
            return null;
        }
    }
}
