namespace Keibai.Core.Alerting;

/// <summary>
/// Pluggable alert sink. The default implementation is <c>ntfy.sh</c> push; SMTP email is opt-in. The
/// registered <see cref="IAlerter"/> is a composite that fans an alert out to every configured provider,
/// so callers just depend on this one interface (mirror of OMD's notification seam for merge).
/// </summary>
public interface IAlerter
{
    /// <summary>Deliver an alert. Implementations must not throw — a dead sink can never break ingestion.</summary>
    Task SendAsync(Alert alert, CancellationToken ct = default);
}
