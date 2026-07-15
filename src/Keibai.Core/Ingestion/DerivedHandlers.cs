using System.Text;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Marten;
using Microsoft.Extensions.Logging;

namespace Keibai.Core.Ingestion;

/// <summary>
/// Rebuilds the derived <see cref="AuctionCase"/> + <see cref="AuctionRound"/> documents from the
/// <see cref="PropertyItem"/> store and links each <see cref="SaleResult"/> back to its property. Pure
/// Marten — it issues NO BIT traffic — so it is safe to run any time and does not ride the sequential
/// ingestion queue.
/// </summary>
public static class RebuildHandler
{
    /// <summary>
    /// Load every property once, group it into cases (court + raw case number) and rounds (court +
    /// 開札期日), upsert both derived document sets, then link sale results to the property they match
    /// (court + normalized case + 物件番号) — inheriting that property's 開札 date.
    /// </summary>
    public static async Task Handle(
        RebuildDerivedDocuments _,
        IKeibaiStoreAccessor store,
        TimeProvider time,
        ILogger<RebuildHandlerMarker> log,
        CancellationToken ct)
    {
        IReadOnlyList<PropertyItem> items;
        IReadOnlyList<SaleResult> results;
        await using (var query = store.QuerySession())
        {
            items = await query.Query<PropertyItem>().ToListAsync(ct).ConfigureAwait(false);
            results = await query.Query<SaleResult>().ToListAsync(ct).ConfigureAwait(false);
        }

        var now = time.GetUtcNow();
        var today = JstClock.Today(time);

        await using var session = store.LightweightSession();

        var cases = BuildCases(items, now);
        foreach (var auctionCase in cases)
        {
            session.Store(auctionCase);
        }

        var rounds = BuildRounds(items, today, now);
        foreach (var round in rounds)
        {
            session.Store(round);
        }

        var linked = LinkSaleResults(items, results, session);

        await session.SaveChangesAsync(ct).ConfigureAwait(false);

        log.LogInformation(
            "RebuildDerivedDocuments: {Cases} cases, {Rounds} rounds, {Linked}/{Results} sale results linked.",
            cases.Count, rounds.Count, linked, results.Count);
    }

    /// <summary>Group items with a parsed <see cref="PropertyItem.Case"/> into <see cref="AuctionCase"/>s.</summary>
    private static List<AuctionCase> BuildCases(IReadOnlyList<PropertyItem> items, DateTimeOffset now)
    {
        var cases = new List<AuctionCase>();
        foreach (var group in items
                     .Where(i => i.Case is not null)
                     .GroupBy(i => (i.CourtId, i.Case!.Raw)))
        {
            var members = group.ToList();
            var first = members[0];
            cases.Add(new AuctionCase
            {
                Id = $"{group.Key.CourtId}:{group.Key.Raw}",
                CourtId = group.Key.CourtId,
                PrefectureId = first.PrefectureId,
                Case = first.Case,
                CaseLabel = group.Key.Raw,
                PropertyItemIds = members.Select(m => m.Id).ToList(),
                PropertyCount = members.Count,
                LastBuilt = now,
            });
        }

        return cases;
    }

    /// <summary>Group items with an <see cref="PropertyItem.OpeningDate"/> into <see cref="AuctionRound"/>s.</summary>
    private static List<AuctionRound> BuildRounds(
        IReadOnlyList<PropertyItem> items, DateOnly today, DateTimeOffset now)
    {
        var rounds = new List<AuctionRound>();
        foreach (var group in items
                     .Where(i => i.OpeningDate is not null)
                     .GroupBy(i => (i.CourtId, OpeningDate: i.OpeningDate!.Value)))
        {
            var members = group.ToList();
            var first = members[0];
            var opening = group.Key.OpeningDate;
            var viewingStart = members.Select(m => m.ViewingStart).FirstOrDefault(d => d is not null);
            var biddingStart = members.Select(m => m.BiddingStart).FirstOrDefault(d => d is not null);
            var biddingEnd = members.Select(m => m.BiddingEnd).FirstOrDefault(d => d is not null);
            var saleDecision = members.Select(m => m.SaleDecisionDate).FirstOrDefault(d => d is not null);

            rounds.Add(new AuctionRound
            {
                Id = $"{group.Key.CourtId}:{opening:yyyy-MM-dd}",
                CourtId = group.Key.CourtId,
                PrefectureId = first.PrefectureId,
                OpeningDate = opening,
                ViewingStart = viewingStart,
                BiddingStart = biddingStart,
                BiddingEnd = biddingEnd,
                SaleDecisionDate = saleDecision,
                PropertyItemIds = members.Select(m => m.Id).ToList(),
                PropertyCount = members.Count,
                Status = RoundStatus.Derive(viewingStart, biddingStart, biddingEnd, opening, today),
                LastBuilt = now,
            });
        }

        return rounds;
    }

    /// <summary>
    /// Link each <see cref="SaleResult"/> to the tracked <see cref="PropertyItem"/> it belongs to,
    /// matching on court + normalized case label + 物件番号, and inherit that property's 開札 date.
    /// Returns the number of results that were matched and stored.
    /// </summary>
    private static int LinkSaleResults(
        IReadOnlyList<PropertyItem> items, IReadOnlyList<SaleResult> results, IDocumentSession session)
    {
        var index = new Dictionary<(string CourtId, string CaseKey), List<PropertyItem>>();
        foreach (var item in items.Where(i => i.Case is not null))
        {
            var key = (item.CourtId, NormalizeCase(item.Case!.Raw));
            if (!index.TryGetValue(key, out var bucket))
            {
                bucket = [];
                index[key] = bucket;
            }

            bucket.Add(item);
        }

        var linked = 0;
        foreach (var result in results)
        {
            if (result.CourtId is null)
            {
                continue;
            }

            var key = (result.CourtId, NormalizeCase(result.CaseLabel));
            if (!index.TryGetValue(key, out var candidates) || candidates.Count == 0)
            {
                continue;
            }

            var match = candidates.FirstOrDefault(c =>
                c.Items.Any(d => d.ItemNo == result.ItemNo)) ?? candidates[0];

            result.PropertyItemId = match.Id;
            result.OpeningDate = match.OpeningDate;
            session.Store(result);
            linked++;
        }

        return linked;
    }

    /// <summary>
    /// Normalize a case label for cross-source matching: drop ASCII and full-width spaces and unify
    /// half/full-width parentheses so spacing/paren variants of <c>令和07年(ケ)第5号</c> collate to one key.
    /// </summary>
    private static string NormalizeCase(string? caseLabel)
    {
        if (string.IsNullOrEmpty(caseLabel))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(caseLabel.Length);
        foreach (var ch in caseLabel)
        {
            switch (ch)
            {
                case ' ':
                case '　': // full-width space
                    break;
                case '（':
                    sb.Append('(');
                    break;
                case '）':
                    sb.Append(')');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>Marker for typed <c>ILogger</c> injection in <see cref="RebuildHandler"/>.</summary>
public sealed class RebuildHandlerMarker;
