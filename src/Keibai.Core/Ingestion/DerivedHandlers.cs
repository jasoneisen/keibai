using System.Text;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Marten;
using Microsoft.Extensions.Logging;

namespace Keibai.Core.Domain
{
    /// <summary>
    /// A derived 事件 (auction case): every tracked <see cref="PropertyItem"/> that shares a court + raw
    /// case number, rolled up so the case can be browsed as one unit. Rebuilt (never hand-edited) from the
    /// property store by <see cref="Ingestion.RebuildHandler"/>; identity is <c>{CourtId}:{Case.Raw}</c> so
    /// a rebuild upserts rather than duplicates.
    /// </summary>
    public sealed class AuctionCase
    {
        /// <summary><c>{CourtId}:{Case.Raw}</c> — the Marten identity.</summary>
        public required string Id { get; set; }
        /// <summary>BIT court code.</summary>
        public required string CourtId { get; set; }
        /// <summary>JIS prefecture code (from the first member item).</summary>
        public string? PrefectureId { get; set; }
        /// <summary>Structured case number (from the first member item).</summary>
        public CaseNumber? Case { get; set; }
        /// <summary>Case number as displayed (raw) — the group key.</summary>
        public string? CaseLabel { get; set; }
        /// <summary>Ids of the <see cref="PropertyItem"/>s bundled under this case.</summary>
        public List<string> PropertyItemIds { get; set; } = [];
        /// <summary>Number of member property items (<c>PropertyItemIds.Count</c>).</summary>
        public int PropertyCount { get; set; }
        /// <summary>When this derived document was last rebuilt.</summary>
        public DateTimeOffset LastBuilt { get; set; }
    }

    /// <summary>
    /// A derived auction round: every tracked <see cref="PropertyItem"/> that shares a court + 開札期日,
    /// rolled up with the round-wide schedule and a coarse lifecycle <see cref="Status"/>. Rebuilt from the
    /// property store by <see cref="Ingestion.RebuildHandler"/>; identity is
    /// <c>{CourtId}:{OpeningDate:yyyy-MM-dd}</c> so a rebuild upserts rather than duplicates.
    /// </summary>
    public sealed class AuctionRound
    {
        /// <summary><c>{CourtId}:{OpeningDate:yyyy-MM-dd}</c> — the Marten identity.</summary>
        public required string Id { get; set; }
        /// <summary>BIT court code.</summary>
        public required string CourtId { get; set; }
        /// <summary>JIS prefecture code (from the first member item).</summary>
        public string? PrefectureId { get; set; }
        /// <summary>開札期日 — the round's bid-opening day (the group key).</summary>
        public DateOnly OpeningDate { get; set; }
        /// <summary>閲覧開始日 (first non-null across the round's members).</summary>
        public DateOnly? ViewingStart { get; set; }
        /// <summary>入札期間 start (first non-null across the round's members).</summary>
        public DateOnly? BiddingStart { get; set; }
        /// <summary>入札期間 end (first non-null across the round's members).</summary>
        public DateOnly? BiddingEnd { get; set; }
        /// <summary>売却決定期日 (first non-null across the round's members).</summary>
        public DateOnly? SaleDecisionDate { get; set; }
        /// <summary>Ids of the <see cref="PropertyItem"/>s opening in this round.</summary>
        public List<string> PropertyItemIds { get; set; } = [];
        /// <summary>Number of member property items (<c>PropertyItemIds.Count</c>).</summary>
        public int PropertyCount { get; set; }
        /// <summary>Coarse lifecycle stage (see <see cref="Ingestion.RoundStatus"/>).</summary>
        public string Status { get; set; } = "unknown";
        /// <summary>When this derived document was last rebuilt.</summary>
        public DateTimeOffset LastBuilt { get; set; }
    }
}

namespace Keibai.Core.Ingestion
{
    /// <summary>
    /// Coarse lifecycle stage of an <see cref="Domain.AuctionRound"/>, derived from its schedule relative
    /// to today (JST): <c>upcoming</c> → <c>viewing</c> → <c>bidding</c> → <c>closed</c> → <c>opened</c>,
    /// with <c>unknown</c> when the schedule is too sparse to place.
    /// </summary>
    public static class RoundStatus
    {
        /// <summary>
        /// Place a round on its lifecycle for <paramref name="today"/>. Uses whichever schedule dates are
        /// present, degrading to <c>unknown</c> when none apply.
        /// </summary>
        public static string Derive(
            DateOnly? viewingStart, DateOnly? biddingStart, DateOnly? biddingEnd,
            DateOnly openingDate, DateOnly today)
        {
            if (today >= openingDate)
            {
                return "opened";
            }

            if (biddingEnd is { } end && today > end)
            {
                return "closed";
            }

            if (biddingStart is { } bidStart && biddingEnd is { } bidEnd
                && today >= bidStart && today <= bidEnd)
            {
                return "bidding";
            }

            if (viewingStart is { } view && today >= view
                && (biddingStart is not { } bs || today < bs))
            {
                return "viewing";
            }

            if (viewingStart is { } vs && today < vs)
            {
                return "upcoming";
            }

            if (viewingStart is null && biddingStart is { } onlyStart && today < onlyStart)
            {
                return "upcoming";
            }

            return "unknown";
        }
    }

    /// <summary>
    /// Rebuilds the derived <see cref="Domain.AuctionCase"/> + <see cref="Domain.AuctionRound"/> documents
    /// from the <see cref="PropertyItem"/> store and links each <see cref="SaleResult"/> back to its
    /// property. Pure Marten — it issues NO BIT traffic — so it is safe to run at any time and does not
    /// ride the sequential ingestion queue.
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

        /// <summary>Group items with a parsed <see cref="PropertyItem.Case"/> into <see cref="Domain.AuctionCase"/>s.</summary>
        private static List<Domain.AuctionCase> BuildCases(IReadOnlyList<PropertyItem> items, DateTimeOffset now)
        {
            var cases = new List<Domain.AuctionCase>();
            foreach (var group in items
                         .Where(i => i.Case is not null)
                         .GroupBy(i => (i.CourtId, i.Case!.Raw)))
            {
                var members = group.ToList();
                var first = members[0];
                cases.Add(new Domain.AuctionCase
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

        /// <summary>Group items with an <see cref="PropertyItem.OpeningDate"/> into <see cref="Domain.AuctionRound"/>s.</summary>
        private static List<Domain.AuctionRound> BuildRounds(
            IReadOnlyList<PropertyItem> items, DateOnly today, DateTimeOffset now)
        {
            var rounds = new List<Domain.AuctionRound>();
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

                rounds.Add(new Domain.AuctionRound
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
        /// half/full-width parentheses so <c>令和07年(ケ)第5号</c> and <c>令和07年（ケ）第5号</c> spacing
        /// variants collate to one key.
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
}
