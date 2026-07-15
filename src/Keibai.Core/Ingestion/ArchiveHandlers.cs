using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Monitoring;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Keibai.Core.Ingestion;

/// <summary>
/// The PDF-archive pipeline (Phase 2 core value: capture the 3点セット before BIT deletes it). Every
/// handler runs on the single sequential <c>keibai-ingestion</c> queue, so all BIT traffic stays
/// 1-req/3s single-threaded and the <see cref="DailyStats"/> increments never race. Archiving is
/// idempotent — content-addressed, so replaying a message never duplicates a document or a download that
/// already produced identical bytes.
/// </summary>
public static class ArchiveHandler
{
    /// <summary>Archive one property's 3点セット (availability-gate → download → validate → store).</summary>
    public static async Task Handle(
        ArchiveDocuments message,
        BitClient client,
        IDocumentBlobStore blobs,
        IKeibaiStoreAccessor store,
        BitBlockResponder blockResponder,
        IOptions<BitOptions> options,
        TimeProvider time,
        ILogger<ArchiveHandlerMarker> log,
        CancellationToken ct)
    {
        var id = PropertyId(message.CourtId, message.SaleUnitId);
        await using var session = store.LightweightSession();
        var item = await session.LoadAsync<PropertyItem>(id, ct).ConfigureAwait(false);
        if (item is null)
        {
            return; // listing handler owns creation; nothing to archive yet
        }

        if (!await EligibleAsync(session, item, options.Value, ct).ConfigureAwait(false))
        {
            return;
        }

        if (item.LastArchivedAt is not null || item.ThreeSetUnavailable)
        {
            return; // already archived, or known-deleted — idempotent no-op (re-checks use RecheckDocuments)
        }

        var stats = await LoadOrCreateStatsAsync(session, JstClock.TodayKey(time)).ConfigureAwait(false);
        stats.ArchiveAttempts++;

        try
        {
            if (!await client.IsThreeSetAvailableAsync(message.CourtId, message.SaleUnitId, ct)
                    .ConfigureAwait(false))
            {
                item.ThreeSetUnavailable = true;
                stats.ThreeSetUnavailable++;
                log.LogWarning("3点セット for {Id} is unavailable (bidding ended / deleted) — cannot archive.", id);
                await SaveAsync(session, item, stats, ct).ConfigureAwait(false);
                return;
            }

            var (bytes, fileName) =
                await client.DownloadThreeSetAsync(message.CourtId, message.SaleUnitId, ct).ConfigureAwait(false);

            if (!ArchivePolicy.IsProbablyPdf(bytes))
            {
                stats.ArchiveFailures++;
                log.LogWarning("Downloaded 3点セット for {Id} is not a valid PDF ({Size} bytes) — not archiving.",
                    id, bytes.Length);
                await SaveAsync(session, item, stats, ct).ConfigureAwait(false);
                return;
            }

            var stored = await StoreVersionAsync(session, blobs, options.Value, item, bytes, fileName, time, ct)
                .ConfigureAwait(false);
            item.LastArchivedAt = time.GetUtcNow();
            if (stored)
            {
                stats.PdfsArchived++;
            }

            await SaveAsync(session, item, stats, ct).ConfigureAwait(false);
            log.LogInformation("Archived 3点セット for {Id}: {Size:N0} bytes.", id, bytes.Length);
        }
        catch (BitBlockedException ex)
        {
            // Stop-and-alert: disable this court, alert, and DO NOT retry around the block.
            await blockResponder.RespondAsync(message.CourtId, $"archive {id}", ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Re-check an archived property once mid-window; a differing hash is kept as a new version.</summary>
    public static async Task Handle(
        RecheckDocuments message,
        BitClient client,
        IDocumentBlobStore blobs,
        IKeibaiStoreAccessor store,
        BitBlockResponder blockResponder,
        IOptions<BitOptions> options,
        TimeProvider time,
        ILogger<ArchiveHandlerMarker> log,
        CancellationToken ct)
    {
        var id = PropertyId(message.CourtId, message.SaleUnitId);
        await using var session = store.LightweightSession();
        var item = await session.LoadAsync<PropertyItem>(id, ct).ConfigureAwait(false);
        if (item is null || item.LastArchivedAt is null || item.LastRecheckedAt is not null)
        {
            return; // never archived, or already re-checked once (the loop is once-per-window)
        }

        if (!await EligibleAsync(session, item, options.Value, ct).ConfigureAwait(false))
        {
            return;
        }

        // Mark re-checked regardless of outcome below, so a property is never re-downloaded twice a window.
        item.LastRecheckedAt = time.GetUtcNow();
        var stats = await LoadOrCreateStatsAsync(session, JstClock.TodayKey(time)).ConfigureAwait(false);
        stats.RechecksPerformed++;

        try
        {
            if (item.ThreeSetUnavailable ||
                !await client.IsThreeSetAvailableAsync(message.CourtId, message.SaleUnitId, ct).ConfigureAwait(false))
            {
                item.ThreeSetUnavailable = true;
                await SaveAsync(session, item, stats, ct).ConfigureAwait(false);
                return;
            }

            var (bytes, fileName) =
                await client.DownloadThreeSetAsync(message.CourtId, message.SaleUnitId, ct).ConfigureAwait(false);
            if (!ArchivePolicy.IsProbablyPdf(bytes))
            {
                stats.ArchiveFailures++;
                await SaveAsync(session, item, stats, ct).ConfigureAwait(false);
                return;
            }

            var stored = await StoreVersionAsync(session, blobs, options.Value, item, bytes, fileName, time, ct)
                .ConfigureAwait(false);
            if (stored)
            {
                stats.AmendmentsCaptured++;
                log.LogInformation("Re-check captured an AMENDED 3点セット for {Id} — kept as a new version.", id);
            }

            await SaveAsync(session, item, stats, ct).ConfigureAwait(false);
        }
        catch (BitBlockedException ex)
        {
            await blockResponder.RespondAsync(message.CourtId, $"re-check {id}", ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Nightly reconciliation + manual backlog drain: enqueue archives (soonest 入札期間 deadline first)
    /// and due mid-window re-checks. Idempotent handlers make any overlap with same-night enqueues a no-op.
    /// </summary>
    public static async Task Handle(
        ScheduleArchiveWork _,
        IKeibaiStoreAccessor store,
        IOptions<BitOptions> options,
        IMessageBus bus,
        TimeProvider time,
        ILogger<ArchiveHandlerMarker> log,
        CancellationToken ct)
    {
        var opts = options.Value;
        var today = JstClock.Today(time);
        var recheckCutoff = time.GetUtcNow().AddDays(-opts.RecheckAfterDays);

        await using var session = store.QuerySession();
        var disabled = (await session.Query<Court>().Where(c => c.CrawlDisabled).ToListAsync(ct)
            .ConfigureAwait(false)).Select(c => c.Id).ToHashSet();

        bool Eligible(PropertyItem p) =>
            ArchivePolicy.PrefectureEligible(p.PrefectureId, opts.ArchivePrefectures)
            && !disabled.Contains(p.CourtId)
            && ArchivePolicy.InWindow(p, today);

        // Archive: un-archived, still-available, in-window — ordered by soonest deletion deadline.
        var toArchive = (await session.Query<PropertyItem>()
                .Where(p => p.LastArchivedAt == null && !p.ThreeSetUnavailable).ToListAsync(ct).ConfigureAwait(false))
            .Where(Eligible)
            .OrderBy(ArchivePolicy.DeadlineKey)
            .ToList();
        foreach (var p in toArchive)
        {
            await bus.PublishAsync(new ArchiveDocuments(p.CourtId, p.SaleUnitId)).ConfigureAwait(false);
        }

        // Re-check: archived ≥ RecheckAfterDays ago, not yet re-checked, still in-window.
        var toRecheck = (await session.Query<PropertyItem>()
                .Where(p => p.LastArchivedAt != null && p.LastRecheckedAt == null && !p.ThreeSetUnavailable)
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(p => p.LastArchivedAt < recheckCutoff && Eligible(p))
            .ToList();
        foreach (var p in toRecheck)
        {
            await bus.PublishAsync(new RecheckDocuments(p.CourtId, p.SaleUnitId)).ConfigureAwait(false);
        }

        log.LogInformation(
            "Archive schedule: {Archive} to archive (deadline-ordered), {Recheck} to re-check.",
            toArchive.Count, toRecheck.Count);
    }

    /// <summary>
    /// Store the bytes content-addressed and record an <see cref="ArchivedDocument"/> if this exact content
    /// is new for the property. Returns true when a NEW version was recorded (false = identical bytes seen
    /// before, i.e. no amendment).
    /// </summary>
    private static async Task<bool> StoreVersionAsync(
        IDocumentSession session, IDocumentBlobStore blobs, BitOptions opts, PropertyItem item,
        byte[] bytes, string? fileName, TimeProvider time, CancellationToken ct)
    {
        var (sha, blobPath) = await blobs.PutAsync(bytes, ".pdf", ct).ConfigureAwait(false);
        var docId = $"{item.Id}:{sha}";
        if (await session.LoadAsync<ArchivedDocument>(docId, ct).ConfigureAwait(false) is not null)
        {
            return false; // this exact content already archived for this property (idempotent)
        }

        var version = await session.Query<ArchivedDocument>()
            .Where(d => d.PropertyItemId == item.Id).CountAsync(ct).ConfigureAwait(false) + 1;
        session.Store(new ArchivedDocument
        {
            Id = docId,
            PropertyItemId = item.Id,
            Sha256 = sha,
            Kind = "combined",
            Version = version,
            ByteSize = bytes.Length,
            SourceUrl = ArchivePolicy.ThreeSetUrl(opts.BaseUrl, item.CourtId, item.SaleUnitId),
            FetchedAt = time.GetUtcNow(),
            BlobPath = blobPath,
            SuggestedFileName = fileName,
        });
        return true;
    }

    private static async Task<bool> EligibleAsync(
        IDocumentSession session, PropertyItem item, BitOptions opts, CancellationToken ct)
    {
        if (!ArchivePolicy.PrefectureEligible(item.PrefectureId, opts.ArchivePrefectures))
        {
            return false;
        }

        var court = await session.LoadAsync<Court>(item.CourtId, ct).ConfigureAwait(false);
        return court?.CrawlDisabled != true;
    }

    private static async Task<DailyStats> LoadOrCreateStatsAsync(IDocumentSession session, string key)
    {
        var stats = await session.LoadAsync<DailyStats>(key).ConfigureAwait(false);
        if (stats is null)
        {
            stats = new DailyStats { Id = key };
        }

        return stats;
    }

    private static async Task SaveAsync(
        IDocumentSession session, PropertyItem item, DailyStats stats, CancellationToken ct)
    {
        session.Store(item);
        session.Store(stats);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string PropertyId(string courtId, string saleUnitId) => $"{courtId}:{saleUnitId}";
}

/// <summary>Marker for typed <c>ILogger</c> injection in the archive handlers.</summary>
public sealed class ArchiveHandlerMarker;
