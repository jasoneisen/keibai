using Microsoft.Extensions.Options;

namespace Keibai.Core.Bit;

/// <summary>
/// Delegating handler that (1) enforces the kill-switch — no request leaves when
/// <see cref="BitOptions.Enabled"/> is false — and (2) routes every outbound BIT request through the
/// single global <see cref="BitRateLimiter"/>. Installed on the BIT <c>HttpClient</c> so nothing can
/// bypass the 1-req/3s rule.
/// </summary>
public sealed class BitRateLimitingHandler : DelegatingHandler
{
    private readonly BitRateLimiter _limiter;
    private readonly IOptionsMonitor<BitOptions> _options;

    /// <summary>Create the handler.</summary>
    public BitRateLimitingHandler(BitRateLimiter limiter, IOptionsMonitor<BitOptions> options)
    {
        _limiter = limiter;
        _options = options;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            throw new IngestionDisabledException(
                "Keibai:Ingestion:Enabled is false — refusing to send a request to BIT.");
        }

        await _limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);

        // The per-attempt timeout starts HERE, after the slot is acquired — queue wait must not count
        // (HttpClient.Timeout is infinite for this client). Surfaced as HttpRequestException so the
        // outer Polly policy treats a genuine network stall as transient and retries it.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.CurrentValue.RequestTimeout);
        try
        {
            return await base.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"BIT request timed out after {_options.CurrentValue.RequestTimeout}.", ex);
        }
    }
}

/// <summary>Thrown when a BIT request is attempted while the ingestion kill-switch is off.</summary>
public sealed class IngestionDisabledException(string message) : InvalidOperationException(message);

/// <summary>Thrown when BIT returns a block-page/403/429 — stop and alert, never retry around it.</summary>
public sealed class BitBlockedException(string message) : Exception(message);

/// <summary>
/// Thrown when BIT answers with its generic エラー page (served as HTTP 200). It means the request
/// itself was invalid — e.g. a bad <c>prefecturesId</c>/block combination — and must surface as a
/// failure. Parsing it as "zero results" silently swallowed all of Hokkaidō once.
/// </summary>
public sealed class BitErrorPageException(string message) : Exception(message);

/// <summary>Detects BIT's generic エラー page.</summary>
public static class BitErrorPage
{
    /// <summary>True when the HTML is BIT's エラー page rather than a real (possibly empty) result.</summary>
    public static bool IsErrorPage(string html) =>
        html.Contains("<title>エラー", StringComparison.Ordinal);
}
