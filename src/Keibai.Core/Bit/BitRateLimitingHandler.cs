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
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Thrown when a BIT request is attempted while the ingestion kill-switch is off.</summary>
public sealed class IngestionDisabledException(string message) : InvalidOperationException(message);

/// <summary>Thrown when BIT returns a block-page/403/429 — stop and alert, never retry around it.</summary>
public sealed class BitBlockedException(string message) : Exception(message);
