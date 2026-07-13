using System.Text;

namespace Keibai;

/// <summary>
/// The single-shared-password gate. HOST middleware only (per the merge rules, no auth logic lives in
/// <c>Keibai.Web</c> components — OMD's cookie auth takes over at merge time). HTTP Basic against the
/// <c>Keibai:Auth:SharedPassword</c> config value; a blank password disables the gate (dev/test).
/// </summary>
public sealed class SharedPasswordMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly string? _password = configuration["Keibai:Auth:SharedPassword"];

    /// <summary>Invoke the gate.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Health check is always open so uptime probes work without credentials.
        if (string.IsNullOrEmpty(_password) || context.Request.Path.StartsWithSegments("/healthz"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (TryGetPassword(context, out var provided) &&
            CryptographicEquals(provided, _password))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"keibai\"";
    }

    private static bool TryGetPassword(HttpContext context, out string password)
    {
        password = string.Empty;
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
            var colon = decoded.IndexOf(':');
            password = colon >= 0 ? decoded[(colon + 1)..] : decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
