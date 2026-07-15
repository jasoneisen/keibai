using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Keibai.Core.Search;
using Marten;

namespace Keibai.Web.Reading;

/// <summary>Marten-backed <see cref="IResultsReader"/> for the past-results explorer.</summary>
public sealed class ResultsReader(IKeibaiStoreAccessor store) : IResultsReader
{
    /// <inheritdoc/>
    public async Task<PagedResult<SaleResultView>> SearchAsync(ResultsQuery query, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var courts = await session.Query<Court>().ToListAsync(ct).ConfigureAwait(false);
        var courtMap = courts.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);

        IQueryable<SaleResult> q = session.Query<SaleResult>();
        if (!string.IsNullOrWhiteSpace(query.CourtId))
        {
            q = q.Where(r => r.CourtId == query.CourtId);
        }
        else if (!string.IsNullOrWhiteSpace(query.PrefectureId))
        {
            var courtIds = courts.Where(c => c.PrefectureId == query.PrefectureId).Select(c => c.Id).ToList();
            q = q.Where(r => r.CourtId != null && courtIds.Contains(r.CourtId));
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            q = q.Where(r => r.Outcome == query.Outcome);
        }

        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);
        var rows = await q
            .OrderByDescending(r => r.CapturedAt)
            .ThenByDescending(r => r.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var views = rows.Select(r => ToView(r, courtMap)).ToList();
        return new PagedResult<SaleResultView>(views, total, page, size);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PrefectureResultStats>> PrefectureStatsAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var courts = await session.Query<Court>().ToListAsync(ct).ConfigureAwait(false);
        var prefByCourt = courts.ToDictionary(c => c.Id, c => c.PrefectureId, StringComparer.Ordinal);
        var results = await session.Query<SaleResult>().ToListAsync(ct).ConfigureAwait(false);

        var stats = new List<PrefectureResultStats>();
        foreach (var group in results
                     .Where(r => r.CourtId != null && prefByCourt.ContainsKey(r.CourtId))
                     .GroupBy(r => prefByCourt[r.CourtId!]))
        {
            var total = group.Count();
            var sold = group.Count(r => IsSold(r.Outcome));
            var ratios = group
                .Where(r => IsSold(r.Outcome) && r.WinningBid is > 0 && r.SaleStandardAmount is > 0)
                .Select(r => (double)r.WinningBid!.Value / r.SaleStandardAmount!.Value)
                .OrderBy(x => x)
                .ToList();

            stats.Add(new PrefectureResultStats(
                group.Key,
                PrefectureNames.Of(group.Key),
                total,
                sold,
                total == 0 ? 0 : (double)sold / total,
                Median(ratios)));
        }

        return stats.OrderBy(s => s.PrefectureId, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc/>
    public async Task<ResultsFacets> GetFacetsAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var courts = await session.Query<Court>().ToListAsync(ct).ConfigureAwait(false);
        var results = await session.Query<SaleResult>().ToListAsync(ct).ConfigureAwait(false);

        var courtIdsWithResults = results
            .Where(r => r.CourtId != null)
            .Select(r => r.CourtId!)
            .ToHashSet(StringComparer.Ordinal);

        var courtOptions = courts
            .Where(c => courtIdsWithResults.Contains(c.Id))
            .OrderBy(c => c.PrefectureId, StringComparer.Ordinal)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new CourtOption(c.Id, c.Name, c.PrefectureId))
            .ToList();

        var prefIds = courtOptions.Select(c => c.PrefectureId).ToHashSet(StringComparer.Ordinal);
        var prefectures = PrefectureNames.Ordered
            .Where(p => prefIds.Contains(p.Code))
            .Select(p => new PrefectureOption(p.Code, p.Name))
            .ToList();

        var outcomes = results
            .Where(r => !string.IsNullOrWhiteSpace(r.Outcome))
            .Select(r => r.Outcome!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return new ResultsFacets(prefectures, courtOptions, outcomes);
    }

    private static SaleResultView ToView(SaleResult r, IReadOnlyDictionary<string, Court> courtMap)
    {
        var court = r.CourtId != null && courtMap.TryGetValue(r.CourtId, out var c) ? c : null;
        double? ratio = r.WinningBid is > 0 && r.SaleStandardAmount is > 0
            ? (double)r.WinningBid.Value / r.SaleStandardAmount.Value
            : null;
        return new SaleResultView(r, court?.Name, PrefectureNames.Of(court?.PrefectureId), ratio, r.PropertyItemId != null);
    }

    // 売却 and 特別売却 both contain "売却"; 不売 / 取下げ do not.
    private static bool IsSold(string? outcome) =>
        outcome is not null && outcome.Contains("売却", StringComparison.Ordinal);

    private static double? Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
