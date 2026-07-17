using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Parsing;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keibai.Core.Ingestion;

/// <summary>
/// Disaster-recovery rebuild: re-materializes the Marten documents from the surviving content-addressed
/// blob store when the Postgres schema was lost. Runs entirely OFFLINE — it walks the blob root, classifies
/// each blob (PDF magic + page <c>&lt;title&gt;</c> markers), and replays the captured bytes through the
/// SAME parsers the live pipeline uses. Issues NO BIT traffic. Every write is idempotent on a natural key
/// and the handler never deletes or overwrites existing data to null, so it is safe to re-run.
///
/// 事故復旧：Postgres スキーマを失っても、生キャプチャ（blob）から Marten 文書を再構築する。BIT へは一切
/// アクセスせず、既存パーサでオフライン再解析する。自然キーで冪等・非破壊なので何度でも再実行できる。
/// </summary>
public static class RebuildFromBlobstoreHandler
{
    private const int BatchSize = 200;

    // Canonical BIT endpoints per blob kind. The originals were not recoverable (only the raw bytes and
    // their mtimes survived), so RawCapture.Url is RECONSTRUCTED to the endpoint that produced the kind.
    private const string DetailUrl = "/app/propertyresult/pr001/h05";
    private const string ListingUrl = "/app/areaselect/ps002/h05";
    private const string ResultsUrl = "/app/peroidsearch/ps007/h08";
    private const string ThreeSetUrlPath = "/app/detail/pd001/h04";

    /// <summary>Rebuild the document store from the blob store. See <see cref="RebuildFromBlobstore"/>.</summary>
    public static async Task Handle(
        RebuildFromBlobstore message,
        IDocumentBlobStore blobs,
        IKeibaiStoreAccessor store,
        IOptions<BitOptions> bitOptions,
        TimeProvider time,
        ILogger<RebuildFromBlobstoreHandlerMarker> log,
        CancellationToken ct)
    {
        var baseUrl = bitOptions.Value.BaseUrl.TrimEnd('/');

        // 1. Walk the blob root and classify every blob, deduping by sha (prefer .pdf > .html > .bin for
        //    the canonical BlobPath the web app serves).
        var blobsBySha = ClassifyBlobs(blobs, ct);
        log.LogInformation("RebuildFromBlobstore: {Count} unique blobs classified from the store.", blobsBySha.Count);

        // 2. RawCapture provenance — one idempotent row per unique sha.
        var raw = await RebuildRawCapturesAsync(store, blobsBySha.Values, baseUrl, ct).ConfigureAwait(false);

        // 3. PropertyItem — fold detail captures (oldest→newest) then listing rows per id.
        var props = await RebuildPropertiesAsync(store, blobs, blobsBySha.Values, time, ct).ConfigureAwait(false);

        // 4. ArchivedDocument — link crawl-log archive lines to PDF blobs by exact byte size.
        var archived = await RebuildArchivedDocumentsAsync(
            store, blobsBySha.Values, message.ArchiveLogPath, baseUrl, ct).ConfigureAwait(false);

        // 5. SaleResult — parse the results pages through the existing parser and upsert.
        var results = await RebuildSaleResultsAsync(store, blobs, blobsBySha.Values, time, ct).ConfigureAwait(false);

        // 6. Court — derive from the court identity carried on listing/detail pages + capture mtimes.
        var courts = await RebuildCourtsAsync(store, blobs, blobsBySha.Values, ct).ConfigureAwait(false);

        // 8. Structured summary.
        log.LogInformation(
            "RebuildFromBlobstore complete. Blobs: {Blobs} unique ({Detail} detail, {Listing} listing, "
            + "{Results} results, {Pdf} pdf, {Skipped} skipped). RawCaptures: {RawNew} new, {RawExisting} "
            + "already present. Properties: {PropWritten} written ({DetailIds} distinct detail ids). "
            + "ArchivedDocuments: {ArchWritten} written, {Orphan} orphan PDFs (RawCapture only, no archive row). "
            + "SaleResults: {ResultsWritten} upserted. Courts: {CourtsWritten} written.",
            blobsBySha.Count, Count(blobsBySha, BlobKind.Detail), Count(blobsBySha, BlobKind.Listing),
            Count(blobsBySha, BlobKind.Results), Count(blobsBySha, BlobKind.Pdf), Count(blobsBySha, BlobKind.Skip),
            raw.New, raw.Existing, props.Written, props.DistinctDetailIds, archived.Written, archived.OrphanPdfs,
            results.Written, courts.Written);
    }

    // --- 1. Classification -------------------------------------------------------------------------------

    /// <summary>How a blob is treated during the rebuild.</summary>
    private enum BlobKind
    {
        /// <summary>3点セット PDF (<c>%PDF</c> magic).</summary>
        Pdf,
        /// <summary>競売物件検索：詳細 — a property-detail page.</summary>
        Detail,
        /// <summary>競売物件検索：結果一覧 — a listing page.</summary>
        Listing,
        /// <summary>売却結果検索：売却結果一覧 — a sale-results page.</summary>
        Results,
        /// <summary>Not domain data (error/empty/probe/areaselect/attribute-less detail): RawCapture only.</summary>
        Skip,
    }

    /// <summary>A classified blob: its canonical path, size, capture time (mtime), kind, and ids.</summary>
    private sealed record ClassifiedBlob(
        string Sha,
        string BlobPath,
        long Size,
        DateTimeOffset CapturedAt,
        BlobKind Kind,
        bool IsPdf,
        string? CourtId,
        string? SaleUnitId,
        string? PrefectureId);

    /// <summary>
    /// Walk the blob store and classify each blob, deduped by sha. Preference for the canonical BlobPath is
    /// .pdf &gt; .html &gt; .bin (identical .bin/.html bytes are one capture — a mid-life extension change).
    /// Classification is by content only (PDF magic, then page <c>&lt;title&gt;</c>), so the rebuild is
    /// durable for future incidents without depending on any external manifest.
    /// </summary>
    private static Dictionary<string, ClassifiedBlob> ClassifyBlobs(IDocumentBlobStore blobs, CancellationToken ct)
    {
        var bySha = new Dictionary<string, ClassifiedBlob>(StringComparer.Ordinal);

        foreach (var (blobPath, mtime) in blobs.EnumerateBlobs())
        {
            ct.ThrowIfCancellationRequested();
            var sha = ShaOf(blobPath);
            if (sha is null)
            {
                continue;
            }

            if (bySha.TryGetValue(sha, out var existing))
            {
                // Same content already seen under another extension: keep the highest-preference path
                // (.pdf > .html > .bin) and the EARLIEST mtime (the original capture time).
                var better = Prefer(blobPath, existing.BlobPath);
                var earliest = mtime < existing.CapturedAt ? mtime : existing.CapturedAt;
                bySha[sha] = existing with { BlobPath = better, CapturedAt = earliest };
                continue;
            }

            var bytes = ReadBlob(blobs, blobPath);
            var (kind, isPdfContent, courtId, saleUnitId, prefectureId) = Classify(bytes);
            bySha[sha] = new ClassifiedBlob(
                sha, blobPath, bytes.LongLength, mtime, kind, isPdfContent, courtId, saleUnitId, prefectureId);
        }

        return bySha;
    }

    private static (BlobKind Kind, bool IsPdf, string? CourtId, string? SaleUnitId, string? PrefectureId)
        Classify(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return (BlobKind.Skip, false, null, null, null); // empty blob — provenance only
        }

        if (StartsWithPdfMagic(bytes))
        {
            return (BlobKind.Pdf, true, null, null, null);
        }

        // Everything else is decoded as HTML and classified by its page <title>.
        var html = Encoding.UTF8.GetString(bytes);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var title = HtmlEntity.DeEntitize(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? string.Empty)
            ?? string.Empty;

        if (title.Contains("競売物件検索：詳細", StringComparison.Ordinal))
        {
            var courtId = HiddenValue(doc, "courtId");
            var saleUnitId = HiddenValue(doc, "saleUnitId");
            var prefectureId = HiddenValue(doc, "prefecturesId");
            // The one attribute-less detail blob (no saleUnitId hidden input) is not a real property → skip
            // as domain data, keep provenance.
            return string.IsNullOrEmpty(saleUnitId) || string.IsNullOrEmpty(courtId)
                ? (BlobKind.Skip, false, courtId, saleUnitId, prefectureId)
                : (BlobKind.Detail, false, courtId, saleUnitId, prefectureId);
        }

        if (title.Contains("競売物件検索：結果一覧", StringComparison.Ordinal))
        {
            return (BlobKind.Listing, false, null, null, HiddenValue(doc, "prefecturesId"));
        }

        if (title.Contains("売却結果検索：売却結果一覧", StringComparison.Ordinal))
        {
            return (BlobKind.Results, false, HiddenValue(doc, "courtId"), null, HiddenValue(doc, "prefecturesId"));
        }

        // areaselect context pages, the エラー page, the 7-byte success probe, etc. — provenance only.
        return (BlobKind.Skip, false, null, null, null);
    }

    // --- 2. RawCapture -----------------------------------------------------------------------------------

    private static async Task<(int New, int Existing)> RebuildRawCapturesAsync(
        IKeibaiStoreAccessor store, IEnumerable<ClassifiedBlob> blobs, string baseUrl, CancellationToken ct)
    {
        // Load existing content hashes once so a re-run skips shas already present (idempotent).
        HashSet<string> present;
        await using (var query = store.QuerySession())
        {
            present = (await query.Query<RawCapture>().Select(c => c.ContentHash).ToListAsync(ct)
                .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        }

        int created = 0, existing = 0, pending = 0;
        await using var session = store.LightweightSession();
        foreach (var blob in blobs)
        {
            ct.ThrowIfCancellationRequested();
            if (!present.Add(blob.Sha))
            {
                existing++;
                continue;
            }

            session.Store(new RawCapture
            {
                Id = Guid.NewGuid(),
                Url = baseUrl + ReconstructedUrl(blob.Kind, blob.IsPdf),
                FetchedAt = blob.CapturedAt, // file mtime = the original capture time
                ContentHash = blob.Sha,
                BlobPath = blob.BlobPath,
                StatusCode = 200,
                ContentType = blob.IsPdf ? "application/pdf" : "text/html",
            });
            created++;

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

        return (created, existing);
    }

    // --- 3. PropertyItem ---------------------------------------------------------------------------------

    private static async Task<(int Written, int DistinctDetailIds)> RebuildPropertiesAsync(
        IKeibaiStoreAccessor store, IDocumentBlobStore blobs, IEnumerable<ClassifiedBlob> classified,
        TimeProvider time, CancellationToken ct)
    {
        var all = classified.ToList();

        // Group detail captures by property id, oldest→newest, so the latest capture wins per field (same
        // ordering the live enrichment fold relies on).
        var detailById = all
            .Where(b => b.Kind == BlobKind.Detail)
            .GroupBy(b => $"{b.CourtId}:{b.SaleUnitId}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.CapturedAt).ToList(), StringComparer.Ordinal);

        // Parse every listing page; a row's latest-mtime capture wins for the listing-only fields.
        var listingRows = ParseListingRows(blobs, all, ct);

        // The full universe of property ids: any detail id plus any listing-row id.
        var ids = new HashSet<string>(detailById.Keys, StringComparer.Ordinal);
        foreach (var key in listingRows.Keys)
        {
            ids.Add(key);
        }

        int written = 0, pending = 0;
        await using var session = store.LightweightSession();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var item = await session.LoadAsync<PropertyItem>(id, ct).ConfigureAwait(false);
            detailById.TryGetValue(id, out var details);
            listingRows.TryGetValue(id, out var listing);

            var parts = id.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var courtId = parts[0];
            var saleUnitId = parts[1];

            // FirstSeen/LastSeen span every capture (detail + listing) we hold for this property.
            var seenTimes = new List<DateTimeOffset>();
            if (details is not null)
            {
                seenTimes.AddRange(details.Select(d => d.CapturedAt));
            }

            if (listing is not null)
            {
                seenTimes.Add(listing.CapturedAt);
            }

            if (item is null)
            {
                // Prefecture is not carried on the listing row itself (the live sweep supplies it); recover
                // it from the page's prefecturesId hidden input. Detail pages carry it too.
                var prefectureId = listing?.PrefectureId
                    ?? details?.Select(d => d.PrefectureId).FirstOrDefault(p => !string.IsNullOrEmpty(p))
                    ?? string.Empty;

                item = new PropertyItem
                {
                    Id = id,
                    SaleUnitId = saleUnitId,
                    CourtId = courtId,
                    PrefectureId = prefectureId,
                    FirstSeen = seenTimes.Count > 0 ? seenTimes.Min() : time.GetUtcNow(),
                    LastSeen = seenTimes.Count > 0 ? seenTimes.Max() : time.GetUtcNow(),
                };
            }
            else if (seenTimes.Count > 0)
            {
                item.FirstSeen = Min(item.FirstSeen, seenTimes.Min());
                item.LastSeen = Max(item.LastSeen, seenTimes.Max());
            }

            // Fold detail captures oldest→newest via the same enrichment the live path uses (latest wins).
            if (details is not null)
            {
                foreach (var detailBlob in details)
                {
                    var bytes = await blobs.GetAsync(detailBlob.BlobPath, ct).ConfigureAwait(false);
                    if (bytes is null)
                    {
                        continue;
                    }

                    PropertyDetail detail;
                    try
                    {
                        detail = DetailParser.Parse(Encoding.UTF8.GetString(bytes));
                    }
                    catch
                    {
                        continue; // defensive: skip an unparseable capture
                    }

                    DetailEnrichment.Apply(item, detail);
                }
            }

            // Fold the winning listing row (create-or-update, coalesce — never null out a set field).
            if (listing is not null)
            {
                var row = listing.Row;
                item.CourtName ??= row.CourtName;
                item.Case ??= row.Case;
                item.SaleCls ??= row.SaleCls;
                item.RawAddress ??= row.RawAddress;
                item.SaleStandardAmount ??= row.SaleStandardAmount;
                if (string.IsNullOrEmpty(item.PrefectureId) && !string.IsNullOrEmpty(listing.PrefectureId))
                {
                    item.PrefectureId = listing.PrefectureId;
                }
            }

            session.Store(item);
            written++;

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

        return (written, detailById.Count);
    }

    /// <summary>A listing row plus the capture it came from (for latest-wins folding + prefecture recovery).</summary>
    private sealed record CapturedListingRow(ListingRow Row, string? PrefectureId, DateTimeOffset CapturedAt);

    /// <summary>
    /// Parse every listing blob and index rows by <c>{CourtId}:{SaleUnitId}</c>, keeping the row from the
    /// latest-mtime capture per id (so the freshest listing state wins, mirroring the live re-sweep).
    /// </summary>
    private static Dictionary<string, CapturedListingRow> ParseListingRows(
        IDocumentBlobStore blobs, IReadOnlyList<ClassifiedBlob> classified, CancellationToken ct)
    {
        var byId = new Dictionary<string, CapturedListingRow>(StringComparer.Ordinal);
        foreach (var blob in classified.Where(b => b.Kind == BlobKind.Listing).OrderBy(b => b.CapturedAt))
        {
            ct.ThrowIfCancellationRequested();
            var bytes = ReadBlob(blobs, blob.BlobPath);
            if (bytes.Length == 0)
            {
                continue;
            }

            ListingPage page;
            try
            {
                page = ListingParser.Parse(Encoding.UTF8.GetString(bytes));
            }
            catch
            {
                continue;
            }

            foreach (var row in page.Rows)
            {
                var id = $"{row.CourtId}:{row.SaleUnitId}";
                // Ascending mtime order means a later assignment always carries a newer capture → last wins.
                byId[id] = new CapturedListingRow(row, blob.PrefectureId, blob.CapturedAt);
            }
        }

        return byId;
    }

    // --- 4. ArchivedDocument -----------------------------------------------------------------------------

    private sealed record ArchiveLogLine(string? PropertyId, long Bytes);

    private static async Task<(int Written, int OrphanPdfs)> RebuildArchivedDocumentsAsync(
        IKeibaiStoreAccessor store, IEnumerable<ClassifiedBlob> classified, string? archiveLogPath,
        string baseUrl, CancellationToken ct)
    {
        var pdfs = classified.Where(b => b.Kind == BlobKind.Pdf).ToList();
        if (archiveLogPath is null || !File.Exists(archiveLogPath))
        {
            // No crawl log: every PDF is an orphan (provenance only, no ArchivedDocument row).
            return (0, pdfs.Count);
        }

        // PDF byte sizes are globally unique, so size is a 1:1 property↔PDF link.
        var pdfBySize = pdfs
            .GroupBy(b => b.Size)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var linkedShas = new HashSet<string>(StringComparer.Ordinal);

        int written = 0, pending = 0;
        await using var session = store.LightweightSession();
        foreach (var line in ReadArchiveLog(archiveLogPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(line.PropertyId) || !pdfBySize.TryGetValue(line.Bytes, out var pdf))
            {
                continue; // no PDF of that exact size survived — cannot link
            }

            var parts = line.PropertyId.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var courtId = parts[0];
            var saleUnitId = parts[1];
            var docId = $"{line.PropertyId}:{pdf.Sha}";
            linkedShas.Add(pdf.Sha);

            if (await session.LoadAsync<ArchivedDocument>(docId, ct).ConfigureAwait(false) is null)
            {
                session.Store(new ArchivedDocument
                {
                    Id = docId,
                    PropertyItemId = line.PropertyId,
                    Sha256 = pdf.Sha,
                    Kind = "combined",
                    Version = 1,
                    ByteSize = pdf.Size,
                    SourceUrl = $"{baseUrl}{ThreeSetUrlPath}?courtId={courtId}&saleUnitId={saleUnitId}",
                    FetchedAt = pdf.CapturedAt,
                    BlobPath = pdf.BlobPath,
                });
                written++;
            }

            // Stamp the owning property's LastArchivedAt — the UI's "has archived documents" signal
            // (search badge + docs filter). Stamped OUTSIDE the doc-exists guard so a re-run repairs
            // properties whose documents were rebuilt before this stamp existed.
            var owner = await session.LoadAsync<PropertyItem>(line.PropertyId, ct).ConfigureAwait(false);
            if (owner is not null && (owner.LastArchivedAt is null || owner.LastArchivedAt < pdf.CapturedAt))
            {
                owner.LastArchivedAt = pdf.CapturedAt;
                session.Store(owner);
            }

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

        // PDFs the crawl log never witnessed: RawCapture provenance only, no ArchivedDocument row.
        var orphans = pdfs.Count(p => !linkedShas.Contains(p.Sha));
        return (written, orphans);
    }

    // --- 5. SaleResult -----------------------------------------------------------------------------------

    private static async Task<(int Written, int Skipped)> RebuildSaleResultsAsync(
        IKeibaiStoreAccessor store, IDocumentBlobStore blobs, IEnumerable<ClassifiedBlob> classified,
        TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        int written = 0, pending = 0;
        await using var session = store.LightweightSession();
        foreach (var blob in classified.Where(b => b.Kind == BlobKind.Results).OrderBy(b => b.CapturedAt))
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await blobs.GetAsync(blob.BlobPath, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                continue;
            }

            var courtId = blob.CourtId;
            SaleResultPage page;
            try
            {
                page = SaleResultParser.Parse(Encoding.UTF8.GetString(bytes));
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(courtId))
            {
                continue; // no court identity on the page → cannot key the result rows
            }

            foreach (var row in page.Rows)
            {
                var id = SaleResult.MakeId(courtId, row.CaseLabel, row.ItemNo);
                var existing = await session.LoadAsync<SaleResult>(id, ct).ConfigureAwait(false);
                var result = existing ?? new SaleResult { Id = id, PropertyItemId = null };
                result.CourtId = courtId;
                result.CaseLabel = row.CaseLabel;
                result.ItemNo = row.ItemNo;
                result.WinningBid = row.WinningBid ?? result.WinningBid;
                result.SaleStandardAmount = row.SaleStandardAmount ?? result.SaleStandardAmount;
                result.BidCount = row.BidCount ?? result.BidCount;
                result.Outcome = row.Outcome ?? result.Outcome;
                result.CapturedAt = now;
                session.Store(result);
                written++;

                if (++pending >= BatchSize)
                {
                    await session.SaveChangesAsync(ct).ConfigureAwait(false);
                    pending = 0;
                }
            }
        }

        if (pending > 0)
        {
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return (written, 0);
    }

    // --- 6. Court ----------------------------------------------------------------------------------------

    private static async Task<(int Written, int Skipped)> RebuildCourtsAsync(
        IKeibaiStoreAccessor store, IDocumentBlobStore blobs, IEnumerable<ClassifiedBlob> classified,
        CancellationToken ct)
    {
        // Accumulate court identity (name/prefecture) + first/last seen from every page that carries it.
        var courts = new Dictionary<string, CourtAccumulator>(StringComparer.Ordinal);

        void Observe(string courtId, string? name, string? prefectureId, DateTimeOffset at)
        {
            if (string.IsNullOrEmpty(courtId))
            {
                return;
            }

            if (!courts.TryGetValue(courtId, out var acc))
            {
                acc = new CourtAccumulator { FirstSeen = at, LastSeen = at };
                courts[courtId] = acc;
            }

            acc.FirstSeen = Min(acc.FirstSeen, at);
            acc.LastSeen = Max(acc.LastSeen, at);
            if (!string.IsNullOrEmpty(name))
            {
                acc.Name = name;
            }

            if (!string.IsNullOrEmpty(prefectureId))
            {
                acc.PrefectureId = prefectureId;
            }
        }

        foreach (var blob in classified.Where(b => b.Kind is BlobKind.Detail or BlobKind.Listing))
        {
            ct.ThrowIfCancellationRequested();

            if (blob.Kind == BlobKind.Detail)
            {
                Observe(blob.CourtId ?? string.Empty, null, blob.PrefectureId, blob.CapturedAt);
                continue;
            }

            // Listing pages carry the court NAME (per row) alongside its code — the only page kind that
            // does — so mine names from parsed rows.
            var bytes = ReadBlob(blobs, blob.BlobPath);
            if (bytes.Length == 0)
            {
                continue;
            }

            ListingPage page;
            try
            {
                page = ListingParser.Parse(Encoding.UTF8.GetString(bytes));
            }
            catch
            {
                continue;
            }

            foreach (var row in page.Rows)
            {
                Observe(row.CourtId, row.CourtName, blob.PrefectureId, blob.CapturedAt);
            }
        }

        int written = 0, pending = 0;
        await using var session = store.LightweightSession();
        foreach (var (courtId, acc) in courts)
        {
            ct.ThrowIfCancellationRequested();
            var court = await session.LoadAsync<Court>(courtId, ct).ConfigureAwait(false);
            if (court is null)
            {
                session.Store(new Court
                {
                    Id = courtId,
                    Name = acc.Name ?? courtId,
                    PrefectureId = acc.PrefectureId ?? string.Empty,
                    IsBranch = acc.Name?.Contains("支部", StringComparison.Ordinal) ?? false,
                    FirstSeen = acc.FirstSeen,
                    LastSeen = acc.LastSeen,
                });
            }
            else
            {
                // Non-destructive fold: widen the seen window, fill a missing name/prefecture only.
                court.FirstSeen = Min(court.FirstSeen, acc.FirstSeen);
                court.LastSeen = Max(court.LastSeen, acc.LastSeen);
                if ((string.IsNullOrEmpty(court.Name) || court.Name == court.Id) && !string.IsNullOrEmpty(acc.Name))
                {
                    court.Name = acc.Name;
                    court.IsBranch = acc.Name.Contains("支部", StringComparison.Ordinal);
                }

                if (string.IsNullOrEmpty(court.PrefectureId) && !string.IsNullOrEmpty(acc.PrefectureId))
                {
                    court.PrefectureId = acc.PrefectureId;
                }

                session.Store(court);
            }

            written++;
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

        return (written, 0);
    }

    private sealed class CourtAccumulator
    {
        public string? Name { get; set; }
        public string? PrefectureId { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    // --- Blob IO + HTML helpers --------------------------------------------------------------------------

    // Reads resolve through the injected blob store, so the rebuild never assumes a physical disk layout.
    // The classification pass is synchronous, so a synchronous read is intentional here (offline replay,
    // one blob at a time — no concurrency to preserve).
    private static byte[] ReadBlob(IDocumentBlobStore blobs, string blobPath) =>
        blobs.GetAsync(blobPath).GetAwaiter().GetResult() ?? [];

    private static string? ShaOf(string blobPath)
    {
        var name = Path.GetFileNameWithoutExtension(blobPath);
        return name.Length == 64 ? name : null;
    }

    private static bool StartsWithPdfMagic(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;

    private static string? HiddenValue(HtmlDocument doc, string name)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//input[@name='{name}']")
                   ?? doc.DocumentNode.SelectSingleNode($"//input[@id='{name}']");
        var v = node?.GetAttributeValue<string?>("value", null);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    // --- Misc helpers ------------------------------------------------------------------------------------

    private static string ReconstructedUrl(BlobKind kind, bool isPdf) => (kind, isPdf) switch
    {
        (BlobKind.Detail, _) => DetailUrl,
        (BlobKind.Listing, _) => ListingUrl,
        (BlobKind.Results, _) => ResultsUrl,
        (_, true) => ThreeSetUrlPath,
        _ => "/", // error/empty/probe/areaselect: base endpoint (URL not recoverable)
    };

    private static string Prefer(string a, string b)
    {
        // .pdf > .html > .bin
        int Rank(string p) => Path.GetExtension(p).ToLowerInvariant() switch
        {
            ".pdf" => 3,
            ".html" => 2,
            _ => 1,
        };

        return Rank(a) >= Rank(b) ? a : b;
    }

    private static int Count(Dictionary<string, ClassifiedBlob> bySha, BlobKind kind) =>
        bySha.Values.Count(b => b.Kind == kind);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static IEnumerable<ArchiveLogLine> ReadArchiveLog(string path, CancellationToken ct)
    {
        foreach (var raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            ArchiveLogLine? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var propertyId = root.TryGetProperty("propertyId", out var pid) ? pid.GetString() : null;
                long bytes = root.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bv) ? bv : 0;
                if (!string.IsNullOrEmpty(propertyId) && bytes > 0)
                {
                    parsed = new ArchiveLogLine(propertyId, bytes);
                }
            }
            catch (JsonException)
            {
                // skip a malformed line
            }

            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }
}

/// <summary>Marker for typed <c>ILogger</c> injection in <see cref="RebuildFromBlobstoreHandler"/>.</summary>
public sealed class RebuildFromBlobstoreHandlerMarker;
