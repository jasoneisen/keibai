using Microsoft.Extensions.Logging;

namespace Keibai.Core.Alerting;

/// <summary>
/// The registered <see cref="IAlerter"/>: fans one alert out to every configured provider. A failure in
/// one sink never blocks the others (each provider already swallows its own errors; this adds a belt).
/// </summary>
public sealed class CompositeAlerter(IReadOnlyList<IAlerter> alerters, ILogger<CompositeAlerter> log)
    : IAlerter
{
    /// <inheritdoc/>
    public async Task SendAsync(Alert alert, CancellationToken ct = default)
    {
        foreach (var alerter in alerters)
        {
            try
            {
                await alerter.SendAsync(alert, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Alerter {Alerter} threw sending '{Title}'.",
                    alerter.GetType().Name, alert.Title);
            }
        }
    }
}
