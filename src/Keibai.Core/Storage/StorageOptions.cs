namespace Keibai.Core.Storage;

/// <summary>
/// Blob-storage watchdog configuration, bound from <c>Keibai:Storage</c>. The deployment target may be
/// disk-constrained (nationwide archiving is 100–500 GB/yr), so the nightly monitor alerts when the blob
/// root grows past <see cref="MaxGigabytes"/>.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>Config section name.</summary>
    public const string SectionName = "Keibai:Storage";

    /// <summary>
    /// Alert threshold for total blob-store size, in gigabytes. Zero or negative disables the watchdog.
    /// </summary>
    public double MaxGigabytes { get; set; } = 50;
}
