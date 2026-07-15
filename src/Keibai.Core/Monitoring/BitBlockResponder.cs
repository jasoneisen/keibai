using Keibai.Core.Alerting;
using Keibai.Core.Bit;
using Keibai.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Keibai.Core.Monitoring;

/// <summary>
/// The stop-and-alert response to a BIT block (403/429/block-page). A block is NEVER retried around: the
/// affected court's crawling is auto-disabled and a critical alert fires immediately. Other courts and
/// prefectures keep running (a per-court switch, complementing the global <c>Keibai:Ingestion:Enabled</c>
/// kill-switch). The operator investigates and clears <see cref="Court.CrawlDisabled"/> by hand.
/// </summary>
public sealed class BitBlockResponder(
    IKeibaiStoreAccessor store, IAlerter alerter, ILogger<BitBlockResponder> log)
{
    /// <summary>
    /// Auto-disable <paramref name="courtId"/> (when known) and raise a critical alert. Call this from a
    /// handler's <see cref="BitBlockedException"/> catch, then STOP that unit of work (do not rethrow into
    /// a retry).
    /// </summary>
    public async Task RespondAsync(
        string? courtId, string context, BitBlockedException ex, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(courtId))
        {
            await using var session = store.LightweightSession();
            var court = await session.LoadAsync<Court>(courtId, ct).ConfigureAwait(false);
            if (court is not null && !court.CrawlDisabled)
            {
                court.CrawlDisabled = true;
                court.CrawlDisabledReason = ex.Message;
                court.CrawlDisabledAt = DateTimeOffset.UtcNow;
                session.Store(court);
                await session.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        log.LogError(ex, "BIT block ({Context}) — court {Court} auto-disabled.", context, courtId ?? "(n/a)");

        var where = string.IsNullOrEmpty(courtId) ? context : $"court {courtId} ({context})";
        await alerter.SendAsync(
            new Alert(
                $"BIT blocked — {where}",
                $"BIT returned a block for {where}: {ex.Message}\n\n"
                + (string.IsNullOrEmpty(courtId)
                    ? "No single court to disable — consider flipping Keibai:Ingestion:Enabled=false."
                    : $"Court {courtId} crawling is auto-disabled. ")
                + "Do NOT retry around it. Investigate UA/IP/rate, then clear CrawlDisabled to resume.",
                AlertSeverity.Critical),
            ct).ConfigureAwait(false);
    }
}
