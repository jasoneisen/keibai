using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keibai.Core.Alerting;

/// <summary>
/// SMTP email alerter (opt-in via <c>Keibai:Alerts:Providers</c> = <c>smtp</c>). Built on the
/// framework <see cref="SmtpClient"/> to avoid a third-party dependency in the merge artifact. Disabled
/// (silently no-ops) when the SMTP host or recipient is blank. Never throws.
/// </summary>
public sealed class SmtpAlerter : IAlerter
{
    private readonly IOptionsMonitor<AlertOptions> _options;
    private readonly ILogger<SmtpAlerter> _log;

    /// <summary>Create the alerter.</summary>
    public SmtpAlerter(IOptionsMonitor<AlertOptions> options, ILogger<SmtpAlerter> log)
    {
        _options = options;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task SendAsync(Alert alert, CancellationToken ct = default)
    {
        var smtp = _options.CurrentValue.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.To))
        {
            _log.LogWarning("SMTP host/recipient not configured — dropping alert: {Title}", alert.Title);
            return;
        }

        try
        {
            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                EnableSsl = smtp.UseTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 30_000,
            };
            if (!string.IsNullOrWhiteSpace(smtp.Username))
            {
                client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);
            }

            using var message = new MailMessage
            {
                From = new MailAddress(smtp.From),
                Subject = $"[keibai/{alert.Severity}] {alert.Title}",
                Body = alert.Body,
            };
            foreach (var to in smtp.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                message.To.Add(to);
            }

            await client.SendMailAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to send SMTP alert '{Title}'.", alert.Title);
        }
    }
}
