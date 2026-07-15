using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keibai.Core.Alerting;

/// <summary>
/// Default alerter: push to <c>ntfy.sh</c> (or a self-hosted ntfy) by POSTing the body to
/// <c>{BaseUrl}/{Topic}</c>. Uses its OWN named <see cref="HttpClient"/> — never the BIT client — so
/// alerts are not rate-limited or blocked by the ingestion kill-switch. Never throws.
/// </summary>
public sealed class NtfyAlerter : IAlerter
{
    /// <summary>Named-client key so this never rides the rate-limited BIT <c>HttpClient</c>.</summary>
    public const string HttpClientName = "keibai-ntfy";

    private readonly IHttpClientFactory _factory;
    private readonly IOptionsMonitor<AlertOptions> _options;
    private readonly ILogger<NtfyAlerter> _log;

    /// <summary>Create the alerter.</summary>
    public NtfyAlerter(
        IHttpClientFactory factory, IOptionsMonitor<AlertOptions> options, ILogger<NtfyAlerter> log)
    {
        _factory = factory;
        _options = options;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task SendAsync(Alert alert, CancellationToken ct = default)
    {
        var ntfy = _options.CurrentValue.Ntfy;
        if (string.IsNullOrWhiteSpace(ntfy.Topic))
        {
            _log.LogWarning("ntfy topic is blank — dropping alert: {Title}", alert.Title);
            return;
        }

        try
        {
            var client = _factory.CreateClient(HttpClientName);
            var url = $"{ntfy.BaseUrl.TrimEnd('/')}/{ntfy.Topic}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(alert.Body, Encoding.UTF8),
            };
            req.Headers.TryAddWithoutValidation("Title", Ascii(alert.Title));
            req.Headers.TryAddWithoutValidation("Priority", Priority(alert.Severity));
            req.Headers.TryAddWithoutValidation("Tags", Tags(alert.Severity));
            if (!string.IsNullOrWhiteSpace(ntfy.Token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ntfy.Token);
            }

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "ntfy returned {Status} publishing alert '{Title}'.", (int)resp.StatusCode, alert.Title);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A dead alert sink must never break ingestion.
            _log.LogWarning(ex, "Failed to publish ntfy alert '{Title}'.", alert.Title);
        }
    }

    private static string Priority(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => "5",
        AlertSeverity.Warning => "4",
        _ => "3",
    };

    private static string Tags(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => "rotating_light",
        AlertSeverity.Warning => "warning",
        _ => "information_source",
    };

    // The ntfy Title header must be latin-1/ASCII-safe; the body (UTF-8) carries any Japanese detail.
    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c < (char)128 ? c : '?');
        }

        return sb.ToString();
    }
}
