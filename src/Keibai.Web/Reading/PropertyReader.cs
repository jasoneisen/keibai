using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Keibai.Core.Search;
using Keibai.Core.Storage;
using Marten;

namespace Keibai.Web.Reading;

/// <summary>
/// Marten-backed <see cref="IPropertyReader"/>. All filtering/sorting goes through the shared
/// <see cref="PropertySearch"/> so search matches the saved-search digest exactly.
/// </summary>
public sealed class PropertyReader(
    IKeibaiStoreAccessor store, IDocumentBlobStore blobs, TimeProvider time) : IPropertyReader
{
    /// <inheritdoc/>
    public async Task<PagedResult<PropertyItem>> SearchAsync(PropertyQuery query, CancellationToken ct = default)
    {
        var today = JstClock.Today(time);
        await using var session = store.QuerySession();

        var filtered = PropertySearch.Apply(session.Query<PropertyItem>(), query, today);
        var total = await filtered.CountAsync(ct).ConfigureAwait(false);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);
        var items = await PropertySearch.OrderFor(filtered, query.Sort)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<PropertyItem>(items, total, page, size);
    }

    /// <inheritdoc/>
    public async Task<PropertyDetailView?> GetDetailAsync(
        string courtId, string saleUnitId, CancellationToken ct = default)
    {
        var id = $"{courtId}:{saleUnitId}";
        await using var session = store.QuerySession();

        var item = await session.LoadAsync<PropertyItem>(id, ct).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var court = await session.LoadAsync<Court>(item.CourtId, ct).ConfigureAwait(false);

        var documents = (await session.Query<ArchivedDocument>()
                .Where(d => d.PropertyItemId == id)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .OrderByDescending(d => d.Version)
            .ThenByDescending(d => d.FetchedAt)
            .ToList();

        // Case siblings: other sale units sharing this court + case label. Computed from the live property
        // store (always current) rather than the derived AuctionCase doc.
        List<PropertyItem> siblings = [];
        var caseLabel = item.Case?.Raw;
        if (!string.IsNullOrWhiteSpace(caseLabel))
        {
            var courtItems = await session.Query<PropertyItem>()
                .Where(p => p.CourtId == item.CourtId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            siblings = courtItems
                .Where(p => p.Id != id && p.Case != null && p.Case.Raw == caseLabel)
                .OrderBy(p => p.SaleUnitId, StringComparer.Ordinal)
                .ToList();
        }

        var result = await session.Query<SaleResult>()
            .Where(r => r.PropertyItemId == id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var status = PropertySearch.DeriveStatus(item, JstClock.Today(time));
        return new PropertyDetailView(
            item, court, PrefectureNames.Of(item.PrefectureId), status, documents, siblings, result);
    }

    /// <inheritdoc/>
    public async Task<SearchFacets> GetFacetsAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var courts = await session.Query<Court>().ToListAsync(ct).ConfigureAwait(false);

        var courtOptions = courts
            .OrderBy(c => c.PrefectureId, StringComparer.Ordinal)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new CourtOption(c.Id, c.Name, c.PrefectureId))
            .ToList();

        var prefIds = courts.Select(c => c.PrefectureId).ToHashSet(StringComparer.Ordinal);
        var prefectures = PrefectureNames.Ordered
            .Where(p => prefIds.Contains(p.Code))
            .Select(p => new PrefectureOption(p.Code, p.Name))
            .ToList();

        return new SearchFacets(prefectures, courtOptions);
    }

    /// <inheritdoc/>
    public async Task<ArchivedPdf?> GetPdfAsync(
        string propertyItemId, string sha256, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var doc = await session.LoadAsync<ArchivedDocument>($"{propertyItemId}:{sha256}", ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var bytes = await blobs.GetAsync(doc.BlobPath, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        var fileName = string.IsNullOrWhiteSpace(doc.SuggestedFileName)
            ? $"{propertyItemId.Replace(':', '_')}_{doc.Kind}.pdf"
            : doc.SuggestedFileName;
        return new ArchivedPdf(bytes, fileName);
    }
}
