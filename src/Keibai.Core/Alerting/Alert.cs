namespace Keibai.Core.Alerting;

/// <summary>How urgent an alert is. Maps to ntfy priority + email subject prefix.</summary>
public enum AlertSeverity
{
    /// <summary>Informational — a noteworthy but non-urgent event.</summary>
    Info = 0,
    /// <summary>Warning — something is off and needs attention soon.</summary>
    Warning = 1,
    /// <summary>Critical — the system is (partly) broken; act now.</summary>
    Critical = 2,
}

/// <summary>
/// One actionable alert. "No news is good news" — an alert is raised ONLY when a human should do
/// something, so every alert carries enough context to act without opening the logs.
/// </summary>
/// <param name="Title">Short subject line.</param>
/// <param name="Body">Actionable detail: what happened, which court/prefecture, what to do.</param>
/// <param name="Severity">Urgency.</param>
public sealed record Alert(string Title, string Body, AlertSeverity Severity = AlertSeverity.Warning);
