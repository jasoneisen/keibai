namespace Keibai.Core.Alerting;

/// <summary>
/// A persisted alert. Every <see cref="Alert"/> sent through the composite is also written here by
/// <see cref="StoringAlerter"/>, so the Phase 3 ops dashboard can show recent alerts without scraping
/// logs. Newest-first by <see cref="At"/>.
/// </summary>
public sealed class AlertLog
{
    /// <summary>Marten identity (guid).</summary>
    public Guid Id { get; set; }
    /// <summary>Alert title.</summary>
    public required string Title { get; set; }
    /// <summary>Alert body.</summary>
    public required string Body { get; set; }
    /// <summary>Severity.</summary>
    public AlertSeverity Severity { get; set; }
    /// <summary>When the alert was raised.</summary>
    public DateTimeOffset At { get; set; }
}
