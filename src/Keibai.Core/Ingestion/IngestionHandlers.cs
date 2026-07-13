using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Parsing;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Keibai.Core.Ingestion;

/// <summary>
/// The ingestion pipeline as Wolverine handlers. All upserts are idempotent on natural keys
/// (court code; <c>{CourtId}:{SaleUnitId}</c>) so re-running any message is safe. Handlers live in
/// <c>Keibai.Core</c> (never the host).
/// </summary>
public static class IngestionHandlers
{
    /// <summary>Fan out a nationwide sweep into one message per prefecture.</summary>
    public static async Task Handle(SyncCourts _, IMessageBus bus)
    {
        foreach (var prefecture in Prefectures.All)
        {
            await bus.PublishAsync(new SyncPrefectureListings(prefecture)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sweep one prefecture's active listings, paging through <c>/app/search</c>, upserting each row's
    /// <see cref="Court"/> and <see cref="PropertyItem"/>, and enqueuing a detail sync per property.
    /// </summary>
    public static async Task Handle(
        SyncPrefectureListings message,
        BitClient client,
        IDocumentBlobStore blobs,
        IKeibaiStoreAccessor store,
        IMessageBus bus,
        TimeProvider time,
        ILogger<SyncPrefectureListingsHandlerMarker> log,
        CancellationToken ct)
    {
        var run = new CrawlRun
        {
            Id = Guid.NewGuid(),
            PrefectureId = message.PrefectureId,
            StartedAt = time.GetUtcNow(),
        };

        var currentPage = 1;
        const int pageSize = 30;
        var total = int.MaxValue;

        try
        {
            while ((currentPage - 1) * pageSize < total)
            {
                var (page, html) = await client
                    .GetPrefectureListingAsync(message.PrefectureId, currentPage, pageSize, ct)
                    .ConfigureAwait(false);
                run.RequestsMade++;
                total = page.TotalCount;

                if (page.Rows.Count == 0)
                {
                    break;
                }

                // Persist the raw results HTML so the detail handler can replay its full form later
                // without re-fetching (BIT 500s on a partial detail POST).
                var (_, htmlBlobPath) = await blobs
                    .PutAsync(System.Text.Encoding.UTF8.GetBytes(html), ".html", ct)
                    .ConfigureAwait(false);

                await using (var session = store.LightweightSession())
                {
                    foreach (var row in page.Rows)
                    {
                        var (isNew, changed) = await UpsertRowAsync(session, message.PrefectureId, row, time)
                            .ConfigureAwait(false);
                        run.ItemsFound++;
                        if (isNew)
                        {
                            run.ItemsNew++;
                        }
                        else if (changed)
                        {
                            run.ItemsChanged++;
                        }
                    }

                    await session.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                foreach (var row in page.Rows)
                {
                    await bus.PublishAsync(new SyncPropertyDetail(
                        message.PrefectureId, row.CourtId, row.SaleUnitId, htmlBlobPath)).ConfigureAwait(false);
                }

                currentPage++;
            }
        }
        catch (BitBlockedException ex)
        {
            run.Errors++;
            run.Notes.Add($"BLOCKED: {ex.Message}");
            log.LogError(ex, "BIT block during prefecture {Prefecture} sweep — parking.", message.PrefectureId);
            await FinishRunAsync(store, run, time, ct).ConfigureAwait(false);
            throw; // let Wolverine park + surface it
        }

        await FinishRunAsync(store, run, time, ct).ConfigureAwait(false);
        log.LogInformation(
            "Prefecture {Prefecture}: {Found} items ({New} new, {Changed} changed), {Requests} requests.",
            message.PrefectureId, run.ItemsFound, run.ItemsNew, run.ItemsChanged, run.RequestsMade);
    }

    /// <summary>Fetch and upsert one property's detail fields (lat/lng, confirmed case number).</summary>
    public static async Task Handle(
        SyncPropertyDetail message,
        BitClient client,
        IDocumentBlobStore blobs,
        IKeibaiStoreAccessor store,
        CancellationToken ct)
    {
        var htmlBytes = await blobs.GetAsync(message.ResultsHtmlBlobPath, ct).ConfigureAwait(false);
        if (htmlBytes is null)
        {
            return; // results HTML expired from the blob store; a re-sweep will re-enqueue
        }

        var resultsHtml = System.Text.Encoding.UTF8.GetString(htmlBytes);
        var detail = await client
            .GetPropertyDetailAsync(resultsHtml, message.CourtId, message.SaleUnitId, ct)
            .ConfigureAwait(false);

        var id = $"{message.CourtId}:{message.SaleUnitId}";
        await using var session = store.LightweightSession();
        var item = await session.LoadAsync<PropertyItem>(id, ct).ConfigureAwait(false);
        if (item is null)
        {
            return; // listing handler owns creation; detail only enriches
        }

        item.Latitude = detail.Latitude ?? item.Latitude;
        item.Longitude = detail.Longitude ?? item.Longitude;
        item.Case ??= detail.Case;
        session.Store(item);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task<(bool IsNew, bool Changed)> UpsertRowAsync(
        IDocumentSession session, string prefectureId, ListingRow row, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var courtId = row.CourtId;

        var court = await session.LoadAsync<Court>(courtId).ConfigureAwait(false);
        if (court is null)
        {
            session.Store(new Court
            {
                Id = courtId,
                Name = row.CourtName ?? courtId,
                PrefectureId = prefectureId,
                IsBranch = row.CourtName?.Contains("支部") ?? false,
                FirstSeen = now,
                LastSeen = now,
            });
        }
        else
        {
            court.LastSeen = now;
            if (!string.IsNullOrEmpty(row.CourtName))
            {
                court.Name = row.CourtName;
                court.IsBranch = row.CourtName.Contains("支部");
            }

            session.Store(court);
        }

        var id = $"{courtId}:{row.SaleUnitId}";
        var existing = await session.LoadAsync<PropertyItem>(id).ConfigureAwait(false);
        if (existing is null)
        {
            session.Store(new PropertyItem
            {
                Id = id,
                SaleUnitId = row.SaleUnitId,
                CourtId = courtId,
                PrefectureId = prefectureId,
                CourtName = row.CourtName,
                Case = row.Case,
                SaleCls = row.SaleCls,
                RawAddress = row.RawAddress,
                SaleStandardAmount = row.SaleStandardAmount,
                FirstSeen = now,
                LastSeen = now,
            });
            return (true, false);
        }

        var changed = existing.SaleStandardAmount != row.SaleStandardAmount
                      || existing.RawAddress != row.RawAddress
                      || existing.SaleCls != row.SaleCls;
        existing.LastSeen = now;
        existing.CourtName = row.CourtName ?? existing.CourtName;
        existing.Case ??= row.Case;
        existing.SaleCls = row.SaleCls ?? existing.SaleCls;
        existing.RawAddress = row.RawAddress ?? existing.RawAddress;
        existing.SaleStandardAmount = row.SaleStandardAmount ?? existing.SaleStandardAmount;
        session.Store(existing);
        return (false, changed);
    }

    private static async Task FinishRunAsync(
        IKeibaiStoreAccessor store, CrawlRun run, TimeProvider time, CancellationToken ct)
    {
        run.FinishedAt = time.GetUtcNow();
        await using var session = store.LightweightSession();
        session.Store(run);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Marker for typed <c>ILogger</c> injection in the prefecture-sweep handler.</summary>
public sealed class SyncPrefectureListingsHandlerMarker;
