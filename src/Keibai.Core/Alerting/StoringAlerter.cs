using Keibai.Core.Bit;
using Microsoft.Extensions.Logging;

namespace Keibai.Core.Alerting;

/// <summary>
/// An <see cref="IAlerter"/> sink that persists each alert as an <see cref="AlertLog"/> document (this is
/// what powers the ops dashboard's "recent alerts"). Like every sink it must never throw — a dead store
/// never breaks the alert fan-out (the composite guards too; this is a belt).
/// </summary>
public sealed class StoringAlerter(
    IKeibaiStoreAccessor store, TimeProvider time, ILogger<StoringAlerter> log) : IAlerter
{
    /// <inheritdoc/>
    public async Task SendAsync(Alert alert, CancellationToken ct = default)
    {
        try
        {
            await using var session = store.LightweightSession();
            session.Store(new AlertLog
            {
                Id = Guid.NewGuid(),
                Title = alert.Title,
                Body = alert.Body,
                Severity = alert.Severity,
                At = time.GetUtcNow(),
            });
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Failed to persist AlertLog for '{Title}'.", alert.Title);
        }
    }
}
