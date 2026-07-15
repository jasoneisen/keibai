using System.Text;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Monitoring;
using Keibai.Core.Parsing;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Keibai.Core.Ingestion;

/// <summary>
/// The 売却結果 (sale-results) pipeline. Runs on the sequential <c>keibai-ingestion</c> queue (BIT
/// traffic). Upserts are idempotent on <see cref="SaleResult.MakeId"/> (court + case + 物件番号) so
/// re-crawling never duplicates. See <c>docs/bit-api.md</c> for the reverse-engineered flow.
/// </summary>
public static class ResultsHandler
{
    private const int PageSize = 10;

    /// <summary>
    /// Sync one court's latest 売却結果 (page 1) — scheduled the evening of a 開札 day, after BIT publishes.
    /// The <paramref name="message"/>'s OpeningDate is advisory; the fetch always captures the court's most
    /// recent results and upserts them idempotently.
    /// </summary>
    public static async Task Handle(
        SyncRoundResults message,
        BitClient client,
        IKeibaiStoreAccessor store,
        BitBlockResponder blockResponder,
        TimeProvider time,
        ILogger<ResultsHandlerMarker> log,
        CancellationToken ct)
    {
        var court = await LoadActiveCourtAsync(store, message.CourtId, ct).ConfigureAwait(false);
        if (court is null)
        {
            return;
        }

        try
        {
            var (page, _) = await client
                .GetCourtSaleResultsFirstPageAsync(court.PrefectureId, court.Id, PageSize, ct)
                .ConfigureAwait(false);
            var upserted = await UpsertResultsAsync(store, court.Id, page.Rows, time, ct).ConfigureAwait(false);
            log.LogInformation(
                "SyncRoundResults court {Court} ({Opening}): {Upserted} results upserted (of {Total} total).",
                court.Id, message.OpeningDate, upserted, page.TotalCount);
        }
        catch (BitBlockedException ex)
        {
            await blockResponder.RespondAsync(court.Id, "results sync", ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Backfill one page of a court's retained 売却結果 history, then enqueue the next page. Per-court /
    /// per-page chunking keeps each handler well under the execution ceiling; spread nationwide over
    /// several nights, this is the biggest crawl the system does.
    /// </summary>
    public static async Task Handle(
        BackfillResults message,
        BitClient client,
        IDocumentBlobStore blobs,
        IKeibaiStoreAccessor store,
        BitBlockResponder blockResponder,
        IMessageBus bus,
        TimeProvider time,
        ILogger<ResultsHandlerMarker> log,
        CancellationToken ct)
    {
        var court = await LoadActiveCourtAsync(store, message.CourtId, ct).ConfigureAwait(false);
        if (court is null)
        {
            return;
        }

        try
        {
            SaleResultPage page;
            string html;
            if (message.Page <= 1)
            {
                (page, html) = await client
                    .GetCourtSaleResultsFirstPageAsync(court.PrefectureId, court.Id, PageSize, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var prevBytes = message.PreviousResultsBlobPath is null
                    ? null
                    : await blobs.GetAsync(message.PreviousResultsBlobPath, ct).ConfigureAwait(false);
                if (prevBytes is null)
                {
                    return; // previous page expired from the blob store; a re-run restarts at page 1
                }

                (page, html) = await client
                    .GetSaleResultsNextPageAsync(Encoding.UTF8.GetString(prevBytes), message.Page, PageSize, ct)
                    .ConfigureAwait(false);
            }

            var upserted = await UpsertResultsAsync(store, court.Id, page.Rows, time, ct).ConfigureAwait(false);
            log.LogInformation(
                "BackfillResults court {Court} page {Page}: {Upserted}/{Rows} rows ({Total} total).",
                court.Id, message.Page, upserted, page.Rows.Count, page.TotalCount);

            // Chunk to the next page while more results remain and the page actually returned rows.
            if (page.Rows.Count > 0 && message.Page * PageSize < page.TotalCount)
            {
                var (_, blobPath) = await blobs
                    .PutAsync(Encoding.UTF8.GetBytes(html), ".html", ct).ConfigureAwait(false);
                await bus.PublishAsync(new BackfillResults(court.Id, message.Page + 1, blobPath))
                    .ConfigureAwait(false);
            }
        }
        catch (BitBlockedException ex)
        {
            await blockResponder.RespondAsync(court.Id, "results backfill", ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fan out the nationwide backfill: one page-1 <see cref="BackfillResults"/> per active court.</summary>
    public static async Task Handle(
        BackfillAllResults _,
        IKeibaiStoreAccessor store,
        IMessageBus bus,
        ILogger<ResultsHandlerMarker> log,
        CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var courts = await session.Query<Court>().Where(c => !c.CrawlDisabled).ToListAsync(ct).ConfigureAwait(false);
        foreach (var court in courts)
        {
            await bus.PublishAsync(new BackfillResults(court.Id)).ConfigureAwait(false);
        }

        log.LogInformation("BackfillAllResults: enqueued {Count} court backfills.", courts.Count);
    }

    /// <summary>
    /// Enqueue one <see cref="SyncRoundResults"/> per distinct round — (court, 開札 date) — whose 開札 fell
    /// in the recent window, so each round's now-published 売却結果 is synced. Fired the evening of 開札
    /// (primary) and again the next morning (catch-up); the sync is idempotent, so overlap is harmless.
    /// </summary>
    public static async Task Handle(
        ScheduleResultsSync _,
        IKeibaiStoreAccessor store,
        IMessageBus bus,
        TimeProvider time,
        ILogger<ResultsHandlerMarker> log,
        CancellationToken ct)
    {
        var today = JstClock.Today(time);
        var from = today.AddDays(-2);

        await using var session = store.QuerySession();
        var candidates = await session.Query<PropertyItem>()
            .Where(p => p.OpeningDate >= from && p.OpeningDate <= today)
            .ToListAsync(ct).ConfigureAwait(false);

        var rounds = DueRounds(candidates, today);
        foreach (var (courtId, openingDate) in rounds)
        {
            await bus.PublishAsync(new SyncRoundResults(courtId, openingDate)).ConfigureAwait(false);
        }

        log.LogInformation("ScheduleResultsSync: {Count} rounds with a recent 開札 → results sync.", rounds.Count);
    }

    /// <summary>Distinct (court, 開札 date) rounds whose 開札 falls in the recent window — one results sync each.</summary>
    internal static IReadOnlyList<(string CourtId, DateOnly OpeningDate)> DueRounds(
        IEnumerable<PropertyItem> items, DateOnly today)
    {
        var from = today.AddDays(-2);
        return items
            .Where(p => p.CourtId is not null && p.OpeningDate is { } d && d >= from && d <= today)
            .Select(p => (p.CourtId, OpeningDate: p.OpeningDate!.Value))
            .Distinct()
            .ToList();
    }

    /// <summary>Upsert a page of result rows idempotently; returns how many were written. Internal for tests.</summary>
    internal static async Task<int> UpsertResultsAsync(
        IKeibaiStoreAccessor store, string courtId, IReadOnlyList<SaleResultRow> rows,
        TimeProvider time, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        await using var session = store.LightweightSession();
        foreach (var row in rows)
        {
            var id = SaleResult.MakeId(courtId, row.CaseLabel, row.ItemNo);
            var existing = await session.LoadAsync<SaleResult>(id, ct).ConfigureAwait(false);
            var result = existing ?? new SaleResult { Id = id, PropertyItemId = null };
            result.CourtId = courtId;
            result.CaseLabel = row.CaseLabel;
            result.ItemNo = row.ItemNo;
            result.WinningBid = row.WinningBid;
            result.SaleStandardAmount = row.SaleStandardAmount;
            result.BidCount = row.BidCount;
            result.Outcome = row.Outcome;
            result.CapturedAt = now;
            session.Store(result);
        }

        var stats = await session.LoadAsync<DailyStats>(JstClock.TodayKey(time), ct).ConfigureAwait(false)
                    ?? new DailyStats { Id = JstClock.TodayKey(time) };
        stats.SaleResultsUpserted += rows.Count;
        session.Store(stats);

        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    private static async Task<Court?> LoadActiveCourtAsync(
        IKeibaiStoreAccessor store, string courtId, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var court = await session.LoadAsync<Court>(courtId, ct).ConfigureAwait(false);
        return court is null || court.CrawlDisabled ? null : court;
    }
}

/// <summary>Marker for typed <c>ILogger</c> injection in the results handlers.</summary>
public sealed class ResultsHandlerMarker;
