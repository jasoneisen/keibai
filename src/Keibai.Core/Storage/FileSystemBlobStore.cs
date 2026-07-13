using System.Security.Cryptography;

namespace Keibai.Core.Storage;

/// <summary>Local-filesystem content-addressed blob store (<c>{root}/{sha[..2]}/{sha}{ext}</c>).</summary>
public sealed class FileSystemBlobStore : IDocumentBlobStore
{
    private readonly string _root;

    /// <summary>Create a store rooted at <paramref name="rootPath"/> (created if missing).</summary>
    public FileSystemBlobStore(string rootPath)
    {
        _root = rootPath;
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc/>
    public async Task<(string Sha256, string Path)> PutAsync(
        ReadOnlyMemory<byte> content, string extension, CancellationToken ct = default)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var ext = NormalizeExtension(extension);
        var relative = Path.Combine(sha[..2], sha + ext);
        var full = Path.Combine(_root, relative);

        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            // Write to a temp file then move, so a crash mid-write never leaves a partial content blob.
            var tmp = full + ".tmp";
            await File.WriteAllBytesAsync(tmp, content.ToArray(), ct).ConfigureAwait(false);
            File.Move(tmp, full, overwrite: true);
        }

        return (sha, relative.Replace(Path.DirectorySeparatorChar, '/'));
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(string path, CancellationToken ct = default)
    {
        var full = Resolve(path);
        return File.Exists(full) ? await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false) : null;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(Resolve(path)));

    private string Resolve(string path) =>
        Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.') ? extension : "." + extension;
    }
}
