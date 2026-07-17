namespace Keibai.Core.Storage;

/// <summary>
/// Content-addressed blob storage for raw captures and archived PDFs. Default impl is the local
/// filesystem; kept swappable for S3-compatible/Azure blob storage later. Paths are
/// <c>{sha256[..2]}/{sha256}{ext}</c>.
/// </summary>
public interface IDocumentBlobStore
{
    /// <summary>
    /// Store bytes content-addressed by their sha256. Idempotent: re-storing identical bytes is a
    /// no-op that returns the same path. Returns the (sha256Hex, blobPath).
    /// </summary>
    Task<(string Sha256, string Path)> PutAsync(
        ReadOnlyMemory<byte> content, string extension, CancellationToken ct = default);

    /// <summary>Read bytes back by blob path. Null when absent.</summary>
    Task<byte[]?> GetAsync(string path, CancellationToken ct = default);

    /// <summary>True when a blob exists at the path.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>Total bytes currently stored across all blobs (powers the storage watchdog).</summary>
    Task<long> GetTotalBytesAsync(CancellationToken ct = default);

    /// <summary>
    /// Enumerate every stored blob as (blobPath, lastWriteTime). Powers the offline blobstore rebuild
    /// (disaster recovery: re-materialize Marten documents from the surviving content-addressed store).
    /// <c>LastWrite</c> is the on-disk mtime — for recovered captures this IS the original capture time.
    /// </summary>
    IEnumerable<(string BlobPath, DateTimeOffset LastWrite)> EnumerateBlobs();
}
