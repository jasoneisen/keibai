using JasperFx.CodeGeneration.Model;
using Wolverine;

namespace Keibai.Core.Composition;

/// <summary>
/// The messaging half of the merge artifact. The standalone host and (later) the OMD host both call
/// <see cref="ConfigureKeibaiMessaging"/> on their single <see cref="WolverineOptions"/> so Keibai's
/// handlers are discovered and its local queues are durable and prefixed <c>keibai-</c>.
/// </summary>
public static class KeibaiMessagingExtensions
{
    /// <summary>
    /// Register Keibai's handler assembly and durable-local-queue policy. At merge time the OMD host
    /// adds exactly this call to its existing <c>UseWolverine</c> block.
    /// </summary>
    public static void ConfigureKeibaiMessaging(this WolverineOptions opts)
    {
        // Discover the handlers that live in Keibai.Core via the marker type.
        opts.Discovery.IncludeAssembly(typeof(KeibaiMarker).Assembly);

        // The BIT client is a typed HttpClient (AddHttpClient<BitClient>) — an opaque factory that
        // Wolverine's codegen can't inline-construct, so it must be resolved by service location. Permit
        // it (with a warning) rather than force-registering the client as a plain type, which would lose
        // the IHttpClientFactory handler pipeline (the rate-limit/kill-switch delegating handler).
        opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;

        // All Keibai background work is durable so a restart never loses an in-flight sweep/detail item.
        opts.Policies.UseDurableLocalQueues();
    }
}
