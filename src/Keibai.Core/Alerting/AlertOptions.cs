namespace Keibai.Core.Alerting;

/// <summary>
/// Alerting configuration, bound from <c>Keibai:Alerts</c>. Default provider is ntfy.sh push (no
/// account needed — just a topic). SMTP is opt-in. "No news is good news": alerts fire only on
/// actionable anomalies, so a healthy system is silent.
/// </summary>
public sealed class AlertOptions
{
    /// <summary>Config section name.</summary>
    public const string SectionName = "Keibai:Alerts";

    /// <summary>
    /// Which alerters to fan out to. Values: <c>ntfy</c>, <c>smtp</c>, <c>log</c>. Default = ntfy. An
    /// empty list falls back to <c>log</c> so a misconfiguration is visible rather than silently dropping
    /// alerts.
    /// </summary>
    public List<string> Providers { get; set; } = ["ntfy"];

    /// <summary>ntfy settings.</summary>
    public NtfyOptions Ntfy { get; set; } = new();

    /// <summary>SMTP settings (used only when <c>smtp</c> is in <see cref="Providers"/>).</summary>
    public SmtpOptions Smtp { get; set; } = new();
}

/// <summary>ntfy.sh push settings.</summary>
public sealed class NtfyOptions
{
    /// <summary>Base URL of the ntfy server.</summary>
    public string BaseUrl { get; set; } = "https://ntfy.sh";

    /// <summary>
    /// Topic to publish to. Anyone who knows the topic can read it, so use a long unguessable string.
    /// The default is a placeholder — CHANGE IT before relying on alerts.
    /// </summary>
    public string Topic { get; set; } = "keibai-alerts-change-me";

    /// <summary>Optional access token (for protected topics). Blank = none.</summary>
    public string? Token { get; set; }
}

/// <summary>SMTP email settings.</summary>
public sealed class SmtpOptions
{
    /// <summary>SMTP host. Blank disables the SMTP alerter even if listed as a provider.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SMTP port.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Use STARTTLS/SSL.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>SMTP username (blank = no auth).</summary>
    public string? Username { get; set; }

    /// <summary>SMTP password.</summary>
    public string? Password { get; set; }

    /// <summary>From address.</summary>
    public string From { get; set; } = "keibai@localhost";

    /// <summary>To address(es), comma-separated.</summary>
    public string To { get; set; } = string.Empty;
}
