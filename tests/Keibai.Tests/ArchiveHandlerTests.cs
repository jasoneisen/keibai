using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Keibai.Core.Alerting;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Keibai.Core.Monitoring;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Keibai.Tests;

[Collection("host")]
public class ArchiveHandlerTests(HostFixture fixture)
{
    private static readonly byte[] ValidPdf = MakePdf(0x41);
    private static readonly byte[] AmendedPdf = MakePdf(0x42);

    [Fact]
    public async Task Archives_a_valid_pdf_content_addressed_and_idempotently()
    {
        var prop = await SeedPropertyAsync("31111", Unique());
        var time = At("2026-07-15T16:00:00+09:00");
        var (client, blobs, store) = BuildClient(available: true, pdf: ValidPdf);

        await ArchiveAsync(prop, client, blobs, store, time);

        await using (var q = fixture.Store.QuerySession())
        {
            var docs = await q.Query<ArchivedDocument>().Where(d => d.PropertyItemId == prop.Id).ToListAsync();
            var doc = Assert.Single(docs);
            Assert.Equal("combined", doc.Kind);
            Assert.Equal(ValidPdf.Length, doc.ByteSize);
            Assert.Equal(1, doc.Version);
            Assert.True(await blobs.ExistsAsync(doc.BlobPath));
            Assert.NotNull((await q.LoadAsync<PropertyItem>(prop.Id))!.LastArchivedAt);
            Assert.Equal(1, (await q.LoadAsync<DailyStats>("2026-07-15"))!.PdfsArchived);
        }

        // Re-run: already archived → no second download, no duplicate document.
        await ArchiveAsync(prop, client, blobs, store, time);
        await using (var q = fixture.Store.QuerySession())
        {
            Assert.Equal(1, await q.Query<ArchivedDocument>().Where(d => d.PropertyItemId == prop.Id).CountAsync());
        }
    }

    [Fact]
    public async Task Rejects_a_non_pdf_download_without_marking_archived()
    {
        var prop = await SeedPropertyAsync("31111", Unique());
        var time = At("2026-07-15T16:00:00+09:00");
        var junk = Encoding.UTF8.GetBytes("<html>not a pdf</html>");
        var (client, blobs, store) = BuildClient(available: true, pdf: junk, pdfContentType: "text/html");

        await ArchiveAsync(prop, client, blobs, store, time);

        await using var q = fixture.Store.QuerySession();
        Assert.Empty(await q.Query<ArchivedDocument>().Where(d => d.PropertyItemId == prop.Id).ToListAsync());
        Assert.Null((await q.LoadAsync<PropertyItem>(prop.Id))!.LastArchivedAt);
        Assert.Equal(1, (await q.LoadAsync<DailyStats>("2026-07-15"))!.ArchiveFailures);
    }

    [Fact]
    public async Task Marks_unavailable_when_the_3set_is_already_deleted()
    {
        var prop = await SeedPropertyAsync("31111", Unique());
        var time = At("2026-07-15T16:00:00+09:00");
        var (client, blobs, store) = BuildClient(available: false, pdf: ValidPdf);

        await ArchiveAsync(prop, client, blobs, store, time);

        await using var q = fixture.Store.QuerySession();
        var stored = await q.LoadAsync<PropertyItem>(prop.Id);
        Assert.True(stored!.ThreeSetUnavailable);
        Assert.Null(stored.LastArchivedAt);
        Assert.Empty(await q.Query<ArchivedDocument>().Where(d => d.PropertyItemId == prop.Id).ToListAsync());
    }

    [Fact]
    public async Task A_block_disables_the_court_alerts_and_does_not_retry()
    {
        var courtId = "88" + Guid.NewGuid().ToString("N")[..3];
        var prop = await SeedPropertyAsync(courtId, Unique());
        await SeedCourtAsync(courtId);
        var time = At("2026-07-15T16:00:00+09:00");
        var alerter = new CapturingAlerter();
        var (client, blobs, store) = BuildClient(available: true, pdf: ValidPdf, block: true);

        await ArchiveAsync(prop, client, blobs, store, time, alerter);

        await using var q = fixture.Store.QuerySession();
        var court = await q.LoadAsync<Court>(courtId);
        Assert.True(court!.CrawlDisabled);
        Assert.NotNull(court.CrawlDisabledReason);
        var alert = Assert.Single(alerter.Alerts);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains(courtId, alert.Title);
        // The property was NOT marked archived (the block aborted the attempt).
        Assert.Null((await q.LoadAsync<PropertyItem>(prop.Id))!.LastArchivedAt);
    }

    [Fact]
    public async Task Recheck_keeps_both_versions_when_the_hash_changed()
    {
        var prop = await SeedPropertyAsync("31111", Unique());
        var time = At("2026-07-15T16:00:00+09:00");

        var (client1, blobs, store) = BuildClient(available: true, pdf: ValidPdf);
        await ArchiveAsync(prop, client1, blobs, store, time);

        time.Advance(TimeSpan.FromDays(8));
        var (client2, _, _) = BuildClient(available: true, pdf: AmendedPdf);
        await RecheckAsync(prop, client2, blobs, store, time);

        await using var q = fixture.Store.QuerySession();
        var docs = await q.Query<ArchivedDocument>().Where(d => d.PropertyItemId == prop.Id)
            .OrderBy(d => d.Version).ToListAsync();
        Assert.Equal(2, docs.Count);
        Assert.Equal([1, 2], docs.Select(d => d.Version));
        Assert.NotEqual(docs[0].Id, docs[1].Id);
        Assert.NotNull((await q.LoadAsync<PropertyItem>(prop.Id))!.LastRecheckedAt);
    }

    [Fact]
    public async Task Recheck_records_no_new_version_when_the_pdf_is_unchanged()
    {
        var prop = await SeedPropertyAsync("31111", Unique());
        var time = At("2026-07-15T16:00:00+09:00");
        var (client, blobs, store) = BuildClient(available: true, pdf: ValidPdf);

        await ArchiveAsync(prop, client, blobs, store, time);
        time.Advance(TimeSpan.FromDays(8));
        await RecheckAsync(prop, client, blobs, store, time);

        await using var q = fixture.Store.QuerySession();
        Assert.Equal(1, await q.Query<ArchivedDocument>().Where(d => d.PropertyItemId == prop.Id).CountAsync());
    }

    // --- helpers ---

    private static string Unique() => "U" + Guid.NewGuid().ToString("N")[..10];

    private static FakeTimeProvider At(string iso) => new(DateTimeOffset.Parse(iso));

    private Task ArchiveAsync(
        PropertyItem prop, BitClient client, IDocumentBlobStore blobs, IKeibaiStoreAccessor store,
        TimeProvider time, IAlerter? alerter = null) =>
        ArchiveHandler.Handle(
            new ArchiveDocuments(prop.CourtId, prop.SaleUnitId), client, blobs, store, Responder(store, alerter),
            ArchiveOptions(), time, NullLogger<ArchiveHandlerMarker>.Instance, CancellationToken.None);

    private Task RecheckAsync(
        PropertyItem prop, BitClient client, IDocumentBlobStore blobs, IKeibaiStoreAccessor store,
        TimeProvider time, IAlerter? alerter = null) =>
        ArchiveHandler.Handle(
            new RecheckDocuments(prop.CourtId, prop.SaleUnitId), client, blobs, store, Responder(store, alerter),
            ArchiveOptions(), time, NullLogger<ArchiveHandlerMarker>.Instance, CancellationToken.None);

    private static BitBlockResponder Responder(IKeibaiStoreAccessor store, IAlerter? alerter) =>
        new(store, alerter ?? new CapturingAlerter(), NullLogger<BitBlockResponder>.Instance);

    private async Task<PropertyItem> SeedPropertyAsync(string courtId, string saleUnitId)
    {
        var item = new PropertyItem
        {
            Id = $"{courtId}:{saleUnitId}",
            SaleUnitId = saleUnitId,
            CourtId = courtId,
            PrefectureId = "88", // dedicated test prefecture (see foundation commit)
            OpeningDate = new DateOnly(2026, 7, 28),
            BiddingEnd = new DateOnly(2026, 7, 22),
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
        };
        await using var session = fixture.Store.LightweightSession();
        session.Store(item);
        await session.SaveChangesAsync();
        return item;
    }

    private async Task SeedCourtAsync(string courtId)
    {
        await using var session = fixture.Store.LightweightSession();
        session.Store(new Court
        {
            Id = courtId, Name = "テスト裁判所", PrefectureId = "88",
            FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync();
    }

    private static IOptions<BitOptions> ArchiveOptions() =>
        Options.Create(new BitOptions
        {
            Enabled = true,
            BaseUrl = "https://www.bit.courts.go.jp",
            ArchivePrefectures = ["88"],
            RecheckAfterDays = 7,
        });

    private (BitClient Client, IDocumentBlobStore Blobs, IKeibaiStoreAccessor Store) BuildClient(
        bool available, byte[] pdf, string pdfContentType = "application/pdf", bool block = false)
    {
        var blobs = fixture.Host.Services.GetRequiredService<IDocumentBlobStore>();
        var store = fixture.Host.Services.GetRequiredService<IKeibaiStoreAccessor>();
        var handler = new BitStubHandler(available, pdf, pdfContentType, block);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.bit.courts.go.jp") };
        var client = new BitClient(http, blobs, store, ArchiveOptions(), NullLogger<BitClient>.Instance);
        return (client, blobs, store);
    }

    private static byte[] MakePdf(byte fill)
    {
        var body = new byte[2048];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(body, 0);
        for (var i = 9; i < body.Length; i++)
        {
            body[i] = fill;
        }

        return body;
    }

    private sealed class CapturingAlerter : IAlerter
    {
        public List<Alert> Alerts { get; } = [];

        public Task SendAsync(Alert alert, CancellationToken ct = default)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class BitStubHandler(bool available, byte[] pdf, string pdfContentType, bool block)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (block)
            {
                // BitClient converts 403 into BitBlockedException.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/pd001/h03", StringComparison.Ordinal))
            {
                return Task.FromResult(Text(available ? "success" : "unavailable"));
            }

            if (path.EndsWith("/pd001/h04", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(pdf);
                content.Headers.ContentType = new MediaTypeHeaderValue(pdfContentType);
                content.Headers.ContentDisposition =
                    new ContentDispositionHeaderValue("attachment") { FileName = "TAC_R08N00012_1.pdf" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Text(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
    }
}
