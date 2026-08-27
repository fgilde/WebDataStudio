using Microsoft.AspNetCore.StaticFiles;

namespace WebDataStudio.Server.Storage;

/// A folder as an object store. Useful in its own right — a mounted volume where files are dropped —
/// and the store the tests use when they have no business starting a container.
///
/// Keys are relative to the root and always use forward slashes, so the same key works here and in a
/// bucket.
public sealed class LocalObjectStore(StorageTarget target) : IObjectStore
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public StorageTarget Target { get; } = target;

    private string Root => Target.Container;

    /// The absolute path for a key, refusing anything that would leave the root. A connection that
    /// hands somebody one folder must not hand them the disk.
    private string PathOf(string key)
    {
        var full = Path.GetFullPath(Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(Root);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"'{key}' is outside this connection's folder");

        return full;
    }

    private string KeyOf(string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(Root), path);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct)
    {
        var directory = PathOf(prefix);

        if (!Directory.Exists(directory))
            return Task.FromResult(new StoragePage([], null));

        // Sorted, so paging by an offset is stable: without an order, "the next page" means nothing.
        var folders = Directory.EnumerateDirectories(directory).OrderBy(p => p, StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(directory).OrderBy(p => p, StringComparer.Ordinal);

        var all = folders
            .Select(path => new StorageEntry(Path.GetFileName(path), KeyOf(path), true, null, null))
            .Concat(files.Select(path =>
            {
                var info = new FileInfo(path);
                return new StorageEntry(info.Name, KeyOf(path), false, info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            }));

        var skip = int.TryParse(cursor, out var parsed) ? parsed : 0;
        var page = all.Skip(skip).Take(max + 1).ToList();

        return Task.FromResult(new StoragePage(
            [.. page.Take(max)],
            page.Count > max ? (skip + max).ToString() : null));
    }

    public Task<StorageObject?> HeadAsync(string key, CancellationToken ct)
    {
        var path = PathOf(key);
        if (!File.Exists(path)) return Task.FromResult<StorageObject?>(null);

        var info = new FileInfo(path);
        ContentTypes.TryGetContentType(path, out var contentType);

        return Task.FromResult<StorageObject?>(new StorageObject(
            key, info.Length, contentType, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            null, null));
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(PathOf(key), FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous));

    public async Task WriteAsync(string key, Stream content, string? contentType, CancellationToken ct)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);

        await content.CopyToAsync(file, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = PathOf(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// A plain path, which DuckDB reads without any extension at all.
    public string SqlUri(string key) => PathOf(key).Replace('\\', '/');

    public string? SecretStatement() => null;
}
