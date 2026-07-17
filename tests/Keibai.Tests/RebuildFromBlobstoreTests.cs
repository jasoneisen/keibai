using System.Text;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Keibai.Tests;

/// <summary>
/// Disaster-recovery rebuild: re-materialize the document store from a temp blob store built out of the
/// real BIT fixtures (+ a fake %PDF blob and a matching archive-log). Exercises the offline handler exactly
/// as the coordinator will run it against production, and asserts every rebuilt document type, idempotency
/// on a second run, and orphan-PDF handling.
/// </summary>
[Collection("host")]
public class RebuildFromBlobstoreTests(HostFixture fixture)
{
    [Fact]
    public async Task Rebuilds_every_document_type_from_the_blobstore_and_is_idempotent()
    {
        var (root, plan) = await BuildBlobstoreAsync();
        var blobs = new FileSystemBlobStore(root);
        var store = fixture.Host.Services.GetRequiredService<IKeibaiStoreAccessor>();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));

        await RunAsync(blobs, store, time, plan.ArchiveLogPath);

        // --- RawCapture: one row per unique blob (every kind, incl. skipped-as-domain ones). ---
        await using (var q = fixture.Store.QuerySession())
        {
            foreach (var sha in plan.AllShas)
            {
                Assert.Equal(1, await q.Query<RawCapture>().Where(c => c.ContentHash == sha).CountAsync());
            }

            // The reconstructed detail-capture URL + mtime FetchedAt round-trip.
            var detailCap = await q.Query<RawCapture>().Where(c => c.ContentHash == plan.DetailSha).SingleAsync();
            Assert.Contains("propertyresult/pr001/h05", detailCap.Url);
            Assert.Equal(200, detailCap.StatusCode);
            Assert.Equal("text/html", detailCap.ContentType);
            Assert.Equal(plan.DetailMtime, detailCap.FetchedAt);

            var pdfCap = await q.Query<RawCapture>().Where(c => c.ContentHash == plan.LinkedPdfSha).SingleAsync();
            Assert.Equal("application/pdf", pdfCap.ContentType);
        }

        // --- PropertyItem: the detail id folds detail fields; listing-only ids fold listing fields. ---
        await using (var q = fixture.Store.QuerySession())
        {
            var detailProp = await q.LoadAsync<PropertyItem>(plan.DetailPropertyId);
            Assert.NotNull(detailProp);
            Assert.Equal("13", detailProp!.PrefectureId); // recovered from prefecturesId hidden input
            Assert.NotNull(detailProp.Latitude); // detail enrichment applied
            Assert.NotEmpty(detailProp.Items); // full per-物件 attribute set
            Assert.Equal(plan.DetailMtime, detailProp.FirstSeen);

            // Every listing row became a property (create-or-update, never delete).
            Assert.True(await q.Query<PropertyItem>().CountAsync() >= plan.ListingIds.Count);
            var listingProp = await q.LoadAsync<PropertyItem>(plan.ListingIds[0]);
            Assert.NotNull(listingProp);
            Assert.Equal("13", listingProp!.PrefectureId);
        }

        // --- ArchivedDocument: linked to the archive-log line by exact PDF byte size. ---
        await using (var q = fixture.Store.QuerySession())
        {
            var doc = await q.LoadAsync<ArchivedDocument>($"{plan.DetailPropertyId}:{plan.LinkedPdfSha}");
            Assert.NotNull(doc);
            Assert.Equal("combined", doc!.Kind);
            Assert.Equal(plan.LinkedPdfSize, doc.ByteSize);
            Assert.Equal(plan.LinkedPdfSha, doc.Sha256);
            Assert.Contains(plan.DetailPropertyId.Split(':')[0], doc.SourceUrl);

            // Linking must also stamp the owner's LastArchivedAt — the UI's "has archived documents"
            // signal (search badge + docs filter). Regression: the first recovery run left it null.
            var owner = await q.LoadAsync<PropertyItem>(plan.DetailPropertyId);
            Assert.Equal(doc.FetchedAt, owner!.LastArchivedAt);

            // The orphan PDF (not witnessed by the log) got a RawCapture but NO ArchivedDocument row.
            Assert.Empty(await q.Query<ArchivedDocument>().Where(d => d.Sha256 == plan.OrphanPdfSha).ToListAsync());
        }

        // --- SaleResult: results page parsed + upserted via MakeId. ---
        int saleResults;
        await using (var q = fixture.Store.QuerySession())
        {
            saleResults = await q.Query<SaleResult>().Where(r => r.CourtId == plan.ResultsCourtId).CountAsync();
            Assert.True(saleResults > 0);
        }

        // --- Court: rebuilt with name (from listing rows) + prefecture + branch flag. ---
        await using (var q = fixture.Store.QuerySession())
        {
            var court = await q.LoadAsync<Court>(plan.ListingCourtId);
            Assert.NotNull(court);
            Assert.False(string.IsNullOrEmpty(court!.Name));
            Assert.Equal("13", court.PrefectureId);
        }

        // --- Idempotency: a second run writes no duplicates. ---
        await RunAsync(blobs, store, time, plan.ArchiveLogPath);
        await using (var q = fixture.Store.QuerySession())
        {
            foreach (var sha in plan.AllShas)
            {
                Assert.Equal(1, await q.Query<RawCapture>().Where(c => c.ContentHash == sha).CountAsync());
            }

            Assert.Equal(1,
                await q.Query<ArchivedDocument>().Where(d => d.Sha256 == plan.LinkedPdfSha).CountAsync());
            Assert.Equal(saleResults,
                await q.Query<SaleResult>().Where(r => r.CourtId == plan.ResultsCourtId).CountAsync());
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Without_an_archive_log_every_pdf_is_an_orphan_with_raw_provenance_only()
    {
        var (root, plan) = await BuildBlobstoreAsync();
        var blobs = new FileSystemBlobStore(root);
        var store = fixture.Host.Services.GetRequiredService<IKeibaiStoreAccessor>();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));

        await RunAsync(blobs, store, time, archiveLogPath: null);

        await using (var q = fixture.Store.QuerySession())
        {
            // PDFs still get RawCapture provenance...
            Assert.Equal(1, await q.Query<RawCapture>().Where(c => c.ContentHash == plan.LinkedPdfSha).CountAsync());
            // ...but with no log, NO ArchivedDocument rows are written for any PDF.
            Assert.Empty(await q.Query<ArchivedDocument>()
                .Where(d => d.Sha256 == plan.LinkedPdfSha || d.Sha256 == plan.OrphanPdfSha).ToListAsync());
        }

        Directory.Delete(root, recursive: true);
    }

    // --- helpers ---

    private static Task RunAsync(
        IDocumentBlobStore blobs, IKeibaiStoreAccessor store, TimeProvider time, string? archiveLogPath) =>
        RebuildFromBlobstoreHandler.Handle(
            new RebuildFromBlobstore(archiveLogPath), blobs, store, RebuildOptions(), time,
            NullLogger<RebuildFromBlobstoreHandlerMarker>.Instance, CancellationToken.None);

    private static IOptions<BitOptions> RebuildOptions() =>
        Options.Create(new BitOptions { BaseUrl = "https://www.bit.courts.go.jp" });

    /// <summary>Details of the temp blob store so the assertions know the ids/shas/sizes to look for.</summary>
    private sealed record BlobstorePlan(
        string ArchiveLogPath,
        List<string> AllShas,
        string DetailSha,
        string DetailPropertyId,
        DateTimeOffset DetailMtime,
        List<string> ListingIds,
        string ListingCourtId,
        string ResultsCourtId,
        string LinkedPdfSha,
        long LinkedPdfSize,
        string OrphanPdfSha);

    private async Task<(string Root, BlobstorePlan Plan)> BuildBlobstoreAsync()
    {
        // The "host" collection shares ONE ephemeral schema, so salt every blob (an ignored HTML comment /
        // a nonce'd PDF byte) with a per-test nonce → unique shas → no cross-test RawCapture collisions.
        var nonce = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "keibai-rebuild-" + nonce);
        var blobs = new FileSystemBlobStore(root);
        var allShas = new List<string>();

        string Salt(string html) => html + $"\n<!-- rebuild-test {nonce} -->";

        async Task<(string Sha, string Path, DateTimeOffset Mtime)> Put(
            byte[] bytes, string ext, DateTimeOffset mtime, bool track = true)
        {
            var (sha, path) = await blobs.PutAsync(bytes, ext);
            File.SetLastWriteTimeUtc(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)),
                mtime.UtcDateTime);
            if (track)
            {
                allShas.Add(sha);
            }

            return (sha, path, mtime);
        }

        // Detail page — its hidden inputs give courtId/saleUnitId/prefecturesId. Rewrite the saleUnitId to a
        // per-test-unique value so the detail property id is FRESH (no collision with other tests that parse
        // this same fixture into the shared schema) — lets us assert FirstSeen == the capture mtime exactly.
        var uniqueSaleUnitId = "9" + nonce[..10];
        var detailFixture = Fixtures.Detail().Replace("00000021309", uniqueSaleUnitId);
        var detailMtime = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var (detailSha, _, _) = await Put(Encoding.UTF8.GetBytes(Salt(detailFixture)), ".bin", detailMtime);
        var detail = Keibai.Core.Parsing.DetailParser.Parse(detailFixture);
        var detailPropertyId = $"{detail.CourtId}:{detail.SaleUnitId}";

        // Listing page — rows carry court name/code; prefecturesId=13.
        await Put(Encoding.UTF8.GetBytes(Salt(Fixtures.TokyoResults())), ".html",
            DateTimeOffset.Parse("2026-07-11T01:00:00Z"));
        var listingPage = Keibai.Core.Parsing.ListingParser.Parse(Fixtures.TokyoResults());
        var listingIds = listingPage.Rows.Select(r => $"{r.CourtId}:{r.SaleUnitId}").ToList();
        var listingCourtId = listingPage.Rows[0].CourtId;

        // Results page — a Tokyo court's 売却結果.
        await Put(Encoding.UTF8.GetBytes(Salt(Fixtures.SaleResultsListing())), ".html",
            DateTimeOffset.Parse("2026-07-12T01:00:00Z"));
        var resultsCourtId = CourtIdOf(Fixtures.SaleResultsListing());

        // Non-domain blobs: an empty blob + the 7-byte success probe → RawCapture only. (Their content is
        // fixed, so their shas are shared across tests — not tracked in AllShas' uniqueness assertions.)
        await Put([], ".bin", DateTimeOffset.Parse("2026-07-09T01:00:00Z"), track: false);
        await Put(Encoding.UTF8.GetBytes("success"), ".bin", DateTimeOffset.Parse("2026-07-09T02:00:00Z"),
            track: false);

        // A linked PDF (unique byte size, witnessed by the archive-log) and an orphan PDF (not witnessed).
        // Salt the fill nonce into the body so each test's PDFs get distinct shas.
        var linkedPdf = MakePdf(nonce, 2048);
        var (linkedSha, _, _) = await Put(linkedPdf, ".pdf", DateTimeOffset.Parse("2026-07-13T05:00:00Z"));
        var orphanPdf = MakePdf(nonce + "orphan", 4096);
        var (orphanSha, _, _) = await Put(orphanPdf, ".pdf", DateTimeOffset.Parse("2026-07-13T06:00:00Z"));

        // Archive-log: one line linking the detail property to the linked PDF by its exact byte size.
        var archiveLogPath = Path.Combine(root, "archive-log.jsonl");
        await File.WriteAllTextAsync(archiveLogPath,
            $"{{\"propertyId\": \"{detailPropertyId}\", \"bytes\": {linkedPdf.Length}}}\n");

        return (root, new BlobstorePlan(
            archiveLogPath, allShas, detailSha, detailPropertyId, detailMtime, listingIds, listingCourtId,
            resultsCourtId, linkedSha, linkedPdf.Length, orphanSha));
    }

    private static string CourtIdOf(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.SelectSingleNode("//input[@name='courtId']")!.GetAttributeValue("value", "");
    }

    private static byte[] MakePdf(string nonce, int size)
    {
        var body = new byte[size];
        var header = Encoding.ASCII.GetBytes("%PDF-1.4\n" + nonce + "\n");
        header.CopyTo(body, 0);
        for (var i = header.Length; i < body.Length; i++)
        {
            body[i] = 0x20;
        }

        return body;
    }
}
