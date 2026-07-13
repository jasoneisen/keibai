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
}
