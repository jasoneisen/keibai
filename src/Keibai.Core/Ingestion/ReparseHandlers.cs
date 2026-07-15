using System.Text;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Parsing;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.Logging;

namespace Keibai.Core.Ingestion;

/// <summary>
/// Offline reparse backfill — replays stored detail HTML through the current parser to populate new
/// fields on existing properties without any BIT traffic (the "replay from RawCapture" pattern).
/// </summary>
public static class ReparseHandler
{
    private const int BatchSize = 200;

    /// <summary>Re-enrich every property we have a stored detail capture for.</summary>
    public static async Task Handle(
        ReparseDetailCaptures _,
        IDocumentBlobStore blobs,
        IKeibaiStoreAccessor store,
        ILogger<ReparseHandlerMarker> log,
        CancellationToken ct)
    {
        await using var query = store.QuerySession();
        var captures = await query.Query<RawCapture>()
            .Where(c => c.Url.Contains("propertyresult/pr001/h05"))
            .OrderBy(c => c.FetchedAt) // oldest first → the latest capture wins per property
            .ToListAsync(ct)
            .ConfigureAwait(false);

        int parsed = 0, enriched = 0, missingBlob = 0, unmatched = 0, pending = 0;
        await using var session = store.LightweightSession();
        foreach (var capture in captures)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await blobs.GetAsync(capture.BlobPath, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                missingBlob++;
                continue;
            }

            PropertyDetail detail;
            try
            {
                detail = DetailParser.Parse(Encoding.UTF8.GetString(bytes));
            }
            catch
            {
                continue; // not a parseable detail page (defensive)
            }

            parsed++;
            if (string.IsNullOrEmpty(detail.SaleUnitId) || string.IsNullOrEmpty(detail.CourtId))
            {
                continue;
            }

            var item = await session.LoadAsync<PropertyItem>($"{detail.CourtId}:{detail.SaleUnitId}", ct)
                .ConfigureAwait(false);
            if (item is null)
            {
                unmatched++;
                continue;
            }

            DetailEnrichment.Apply(item, detail);
            session.Store(item);
            enriched++;

            if (++pending >= BatchSize)
            {
                await session.SaveChangesAsync(ct).ConfigureAwait(false);
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        log.LogInformation(
            "ReparseDetailCaptures: {Captures} detail captures → {Parsed} parsed, {Enriched} properties "
            + "enriched, {Unmatched} unmatched, {Missing} missing-blob.",
            captures.Count, parsed, enriched, unmatched, missingBlob);
    }
}

/// <summary>Marker for typed <c>ILogger</c> injection in the reparse handler.</summary>
public sealed class ReparseHandlerMarker;
