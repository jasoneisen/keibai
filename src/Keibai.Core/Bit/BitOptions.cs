namespace Keibai.Core.Bit;

/// <summary>
/// All BIT ingestion configuration. Bound from the <c>Keibai:Ingestion</c> section so appsettings
/// merge into the OMD host by concatenation.
/// </summary>
public sealed class BitOptions
{
    /// <summary>Config section name.</summary>
    public const string SectionName = "Keibai:Ingestion";

    /// <summary>
    /// Master kill-switch. When false, the BIT client refuses every outbound request — the single
    /// config toggle that stops all traffic to the court system.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Base address of the BIT site.</summary>
    public string BaseUrl { get; set; } = "https://www.bit.courts.go.jp";

    /// <summary>Honest, stable User-Agent. Never rotate this.</summary>
    public string UserAgent { get; set; } = "keibai-personal-archive/0.1";

    /// <summary>Minimum spacing between BIT requests. Non-negotiable floor: 3 seconds.</summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Max Polly retries before parking the work item and alerting.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Timeout for a single BIT request, measured from AFTER the rate-limit slot is acquired. The
    /// HttpClient's own Timeout is infinite: it would otherwise count time spent queued behind the
    /// global 1-req/3s gate, cancelling every request whose queue wait exceeded it once a sweep's
    /// backlog grew past ~40 requests.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Prefectures whose PDFs to archive (Phase 2). Empty = all. Nationwide archiving is 100–500 GB/yr,
    /// so a disk-constrained deployment limits this to selected prefectures (e.g. <c>["13"]</c> Tokyo).
    /// </summary>
    public List<string> ArchivePrefectures { get; set; } = [];

    /// <summary>
    /// Days after the first archive to re-check a property's 3点セット for mid-window amendments (once).
    /// If the re-fetched bytes hash differently, the new version is archived alongside the original.
    /// </summary>
    public int RecheckAfterDays { get; set; } = 7;
}
