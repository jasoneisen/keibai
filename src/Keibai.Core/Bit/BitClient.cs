using System.Net;
using System.Text;
using Keibai.Core.Parsing;
using Keibai.Core.Storage;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keibai.Core.Bit;

/// <summary>
/// Typed client over BIT's reverse-engineered request flow (see <c>docs/bit-api.md</c>). Every response
/// is captured raw (blob + <see cref="Domain.RawCapture"/>) BEFORE parsing so a parser bug never
/// requires a re-fetch. The rate limit + kill-switch live in <see cref="BitRateLimitingHandler"/> on the
/// injected <see cref="HttpClient"/>, so this class never touches timing.
/// </summary>
public sealed class BitClient
{
    private readonly HttpClient _http;
    private readonly IDocumentBlobStore _blobs;
    private readonly IKeibaiStoreAccessor _store;
    private readonly BitOptions _options;
    private readonly ILogger<BitClient> _log;

    /// <summary>Create the client.</summary>
    public BitClient(
        HttpClient http,
        IDocumentBlobStore blobs,
        IKeibaiStoreAccessor store,
        IOptions<BitOptions> options,
        ILogger<BitClient> log)
    {
        _http = http;
        _blobs = blobs;
        _store = store;
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// Fetch the FIRST page of a prefecture's active listing via the search endpoint. Returns the parsed
    /// page AND the raw results HTML (the detail + paging endpoints both require the full
    /// <c>propertyResultForm</c> carried forward from this exact response — BIT 500s on a partial body).
    /// </summary>
    public async Task<(ListingPage Page, string Html)> GetPrefectureFirstPageAsync(
        string prefectureId, int pageSize, CancellationToken ct = default)
    {
        var pairs = BitForms.PrefectureSearchPairs(prefectureId, 1, pageSize);
        var html = await PostPairsForStringAsync("/app/areaselect/ps002/h05", pairs, ct).ConfigureAwait(false);
        return (ListingParser.Parse(html), html);
    }

    /// <summary>
    /// Fetch a subsequent listing page via the pager endpoint <c>/app/propertyresult/pr001/h39</c>,
    /// replaying the previous page's full <c>propertyResultForm</c> envelope with <c>currentPage</c> set.
    /// </summary>
    public async Task<(ListingPage Page, string Html)> GetPrefectureNextPageAsync(
        string previousPageHtml, int currentPage, int pageSize, CancellationToken ct = default)
    {
        var form = BitForms.ListingPage(previousPageHtml, currentPage, pageSize);
        var html = await PostFormForStringAsync("/app/propertyresult/pr001/h39", form, ct).ConfigureAwait(false);
        return (ListingParser.Parse(html), html);
    }

    /// <summary>
    /// Fetch a single property's detail page, parsed. <paramref name="resultsHtml"/> is the raw results
    /// page the property was found on (its full form is replayed with the detail fields overridden).
    /// </summary>
    public async Task<PropertyDetail> GetPropertyDetailAsync(
        string resultsHtml, string courtId, string saleUnitId, CancellationToken ct = default)
    {
        var form = BitForms.PropertyDetail(resultsHtml, courtId, saleUnitId);
        var html = await PostFormForStringAsync("/app/propertyresult/pr001/h05", form, ct)
            .ConfigureAwait(false);
        return DetailParser.Parse(html);
    }

    /// <summary>
    /// Fetch the FIRST page of a court's 売却結果 (sale results). Two BIT requests: the prefecture-select
    /// step (<c>ps007/h02</c>, which yields the opaque <c>fiscalYear</c>/<c>codeCls</c> the search needs)
    /// then the by-court search (<c>ps007/h08</c>, EMPTY saleScdId = the court's full retained history).
    /// Returns the parsed page AND the raw results HTML (the pager replays its <c>resultDetailForm</c>).
    /// </summary>
    public async Task<(SaleResultPage Page, string Html)> GetCourtSaleResultsFirstPageAsync(
        string prefectureId, string courtId, int pageSize, CancellationToken ct = default)
    {
        var contextHtml = await PostPairsForStringAsync(
            "/app/peroidsearch/ps007/h02", ResultForms.PrefecturePairs(prefectureId), ct).ConfigureAwait(false);
        var context = ResultForms.ParseCourtContext(contextHtml);

        var pairs = ResultForms.CourtSearchPairs(
            prefectureId, courtId, context.FiscalYear, context.CodeCls, pageSize);
        var html = await PostPairsForStringAsync("/app/peroidsearch/ps007/h08", pairs, ct).ConfigureAwait(false);
        return (SaleResultParser.Parse(html), html);
    }

    /// <summary>
    /// Fetch a subsequent 売却結果 page via the pager <c>resultlist/pr002/h03</c>, replaying the previous
    /// page's full <c>resultDetailForm</c> envelope with <c>currPage</c> set.
    /// </summary>
    public async Task<(SaleResultPage Page, string Html)> GetSaleResultsNextPageAsync(
        string previousResultsHtml, int page, int pageSize, CancellationToken ct = default)
    {
        var form = ResultForms.PagerForm(previousResultsHtml, page, pageSize);
        var html = await PostFormForStringAsync("/app/resultlist/pr002/h03", form, ct).ConfigureAwait(false);
        return (SaleResultParser.Parse(html), html);
    }

    /// <summary>
    /// Check whether a 3点セット PDF is still downloadable (BIT deletes them when bidding ends).
    /// Returns true when the availability endpoint answers "success".
    /// </summary>
    public async Task<bool> IsThreeSetAvailableAsync(
        string courtId, string saleUnitId, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string> { ["courtId"] = courtId, ["saleUnitId"] = saleUnitId };
        var body = await PostFormForStringAsync("/app/detail/pd001/h03", form, ct).ConfigureAwait(false);
        return body.Contains("success", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Download the 3点セット PDF bytes (the archival core). Raw-captured before returning.</summary>
    public async Task<(byte[] Bytes, string? FileName)> DownloadThreeSetAsync(
        string courtId, string saleUnitId, CancellationToken ct = default)
    {
        var url = $"/app/detail/pd001/h04?courtId={courtId}&saleUnitId={saleUnitId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        await CaptureAsync(url, resp, bytes, ct).ConfigureAwait(false);
        var fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        return (bytes, fileName);
    }

    private Task<string> PostFormForStringAsync(
        string path, IReadOnlyDictionary<string, string> form, CancellationToken ct) =>
        PostPairsForStringAsync(path, form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)), ct);

    private async Task<string> PostPairsForStringAsync(
        string path, IEnumerable<KeyValuePair<string, string>> pairs, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(pairs),
        };
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        await CaptureAsync(path, resp, bytes, ct).ConfigureAwait(false);
        var html = Encoding.UTF8.GetString(bytes);

        // BIT serves its generic エラー page as HTTP 200. That is an invalid REQUEST, never an empty
        // result — parsing it as "0 rows" silently dropped all of Hokkaidō (prefecturesId=01 instead
        // of the 91–94 pseudo-codes). Raw bytes are already captured above for the post-mortem.
        if (BitErrorPage.IsErrorPage(html))
        {
            throw new BitErrorPageException(
                $"BIT returned its エラー page for {path} — invalid request (bad prefecturesId/block?), not an empty result.");
        }

        return html;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        // A block is a hard stop: never retry around 403/429. Surface it so the handler alerts + parks.
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            _log.LogWarning("BIT returned {Status} for {Url} — treating as a block.",
                (int)resp.StatusCode, req.RequestUri);
            throw new BitBlockedException(
                $"BIT returned {(int)resp.StatusCode} for {req.RequestUri}. Stopping — do not retry around a block.");
        }

        return resp;
    }

    private async Task CaptureAsync(string url, HttpResponseMessage resp, byte[] bytes, CancellationToken ct)
    {
        // Store raw bytes BEFORE any parse. Content-addressed, so identical repeat responses de-dupe.
        var ext = resp.Content.Headers.ContentType?.MediaType == "application/pdf" ? ".pdf" : ".bin";
        var (sha, blobPath) = await _blobs.PutAsync(bytes, ext, ct).ConfigureAwait(false);

        await using var session = _store.LightweightSession();
        session.Store(new Domain.RawCapture
        {
            Id = Guid.NewGuid(),
            Url = _options.BaseUrl + url,
            FetchedAt = DateTimeOffset.UtcNow,
            ContentHash = sha,
            BlobPath = blobPath,
            StatusCode = (int)resp.StatusCode,
            ContentType = resp.Content.Headers.ContentType?.ToString(),
        });
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Thin seam over the ancillary <c>IKeibaiStore</c> so <c>Keibai.Core</c> need not reference the generated
/// Marten store type directly (the host wires this to <c>IKeibaiStore</c>). Keeps Core host-agnostic.
/// </summary>
public interface IKeibaiStoreAccessor
{
    /// <summary>Open a lightweight session on the Keibai ancillary store.</summary>
    IDocumentSession LightweightSession();

    /// <summary>Open a query session on the Keibai ancillary store.</summary>
    IQuerySession QuerySession();
}
