namespace WebDataStudio.Server.Storage;

/// One thing in a listing: either a prefix (what a folder looks like in a key space that has no
/// folders) or an object.
public sealed record StorageEntry(
    string Name, string Key, bool IsPrefix, long? SizeBytes, DateTimeOffset? Modified);

/// One page of a listing, and how to ask for the next. A bucket is never walked, so there is always
/// a cursor rather than a promise that this was everything.
public sealed record StoragePage(IReadOnlyList<StorageEntry> Entries, string? Cursor);

/// What is known about one object without reading it.
public sealed record StorageObject(
    string Key, long SizeBytes, string? ContentType, DateTimeOffset? Modified, string? ETag,
    string? StorageClass);

/// One object store, whichever it is. Nothing above this interface knows whether it is talking to
/// S3, Azure, Google or a folder — which is what keeps the tree, the preview and the query path from
/// growing a branch per provider.
public interface IObjectStore
{
    StorageTarget Target { get; }

    /// One page of what is directly under `prefix`: the prefixes first, then the objects.
    Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct);

    /// What is known about one object, or null when there is no such object.
    Task<StorageObject?> HeadAsync(string key, CancellationToken ct);

    /// The object's bytes. The caller disposes.
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    Task WriteAsync(string key, Stream content, string? contentType, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    /// The URI DuckDB reads this object by: `s3://`, `az://`, `gs://`, or a plain path.
    string SqlUri(string key);

    /// The `CREATE SECRET` DuckDB needs before it can read this store, or null when it needs none —
    /// a local folder, or a store reached through the machine's own identity where DuckDB can use
    /// the same credential chain.
    string? SecretStatement();
}

/// The bucket or container itself is not there.
///
/// Every provider says this in its own words, and all of them say it in a page of XML or JSON that
/// nobody should have to read to learn one sentence. A connection can name a container that was
/// never created — an app host that declared the account but not the container, a name with a typo
/// — and the answer is that sentence, not the wire format.
public sealed class StorageContainerMissingException(string container, Exception? inner = null)
    : Exception($"there is no container called '{container}' here — it has to be created first, " +
                "or the connection has to name one that exists", inner)
{
    public string Container { get; } = container;
}
