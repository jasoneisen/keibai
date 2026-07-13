using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace Keibai.Core.Bit;

/// <summary>
/// Exponential-backoff retry for transient BIT failures (5xx, network). Bounded by
/// <see cref="BitOptions.MaxRetries"/>. Deliberately does NOT retry 403/429 — those are blocks the
/// <see cref="BitClient"/> converts to <see cref="BitBlockedException"/> to stop-and-alert, and a block
/// must never be retried around.
/// </summary>
public static class BitRetryPolicy
{
    /// <summary>Build the Polly policy from options in the container.</summary>
    public static IAsyncPolicy<HttpResponseMessage> Create(IServiceProvider sp)
    {
        var maxRetries = sp.GetRequiredService<IOptions<BitOptions>>().Value.MaxRetries;
        // HandleTransientHttpError covers 5xx + 408 + HttpRequestException. 403/429 are NOT transient
        // here — the BitClient converts them to BitBlockedException before any retry runs — so they are
        // deliberately absent from this policy.
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                maxRetries,
                attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
    }
}
