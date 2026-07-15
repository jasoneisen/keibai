using Microsoft.Extensions.Logging;

namespace Keibai.Core.Alerting;

/// <summary>
/// Alerter that writes to the log. Always part of the composite so there is a durable local trail of
/// every alert even when the push/email sinks are down or unconfigured.
/// </summary>
public sealed class LoggingAlerter(ILogger<LoggingAlerter> log) : IAlerter
{
    /// <inheritdoc/>
    public Task SendAsync(Alert alert, CancellationToken ct = default)
    {
        var level = alert.Severity switch
        {
            AlertSeverity.Critical => LogLevel.Error,
            AlertSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        log.Log(level, "ALERT [{Severity}] {Title}: {Body}", alert.Severity, alert.Title, alert.Body);
        return Task.CompletedTask;
    }
}
