using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace WebDataStudio.Server.Storage;

/// Google Cloud Storage.
///
/// One thing to know, and it is in the documentation rather than hidden here: DuckDB reads `gs://`
/// through the S3 protocol, which wants **HMAC keys**. With a service account alone the tree, the
/// preview and the download all work and a query does not — so a connection meant for querying
/// carries `?hmac=` and `?hmacsecret=` as well.
public sealed class GcsObjectStore : IObjectStore
{
    private readonly Lazy<StorageClient> _lazy;

    public StorageTarget Target { get; }

    /// The client is made on first use, not here. Naming a connection is not the same as being able
    /// to reach it: a bad credential should show up when somebody opens the bucket, with a message,
    /// rather than stopping the connection from existing at all.
    public GcsObjectStore(StorageTarget target)
    {
        Target = target;

        _lazy = new Lazy<StorageClient>(() => target.Option("credentials") is { Length: > 0 } json
            ? StorageClient.Create(GoogleCredential.FromJson(json))
            // Application default credentials: the workload identity of whatever this runs on.
            : StorageClient.Create());
    }

    private StorageClient Client => _lazy.Value;

    public async Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct)
    {
        var key = Target.KeyOf(prefix);
        var search = key.Length == 0 ? null : key.TrimEnd('/') + "/";

        var entries = new List<StorageEntry>();
        string? next = null;

        var options = new ListObjectsOptions { Delimiter = "/", PageSize = max, PageToken = cursor };

        Google.Apis.Storage.v1.Data.Objects? page;

        try
        {
            page = await Client
                .ListObjectsAsync(Target.Container, search, options)
                .AsRawResponses()
                .FirstOrDefaultAsync(ct);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new StorageContainerMissingException(Target.Container, e);
        }

        if (page is null) return new StoragePage(entries, null);

        // Prefixes come back on their own list; a flat key space has no folders of its own.
        foreach (var folder in page.Prefixes ?? [])
            entries.Add(new StorageEntry(Name(folder), Relative(folder), true, null, null));

        foreach (var item in page.Items ?? [])
        {
            if (item.Name.EndsWith('/')) continue;

            entries.Add(new StorageEntry(Name(item.Name), Relative(item.Name), false,
                (long?)item.Size, item.UpdatedDateTimeOffset));
        }

        next = page.NextPageToken;
        return new StoragePage(entries, string.IsNullOrEmpty(next) ? null : next);
    }

    public async Task<StorageObject?> HeadAsync(string key, CancellationToken ct)
    {
        try
        {
            var found = await Client.GetObjectAsync(Target.Container, Target.KeyOf(key),
                cancellationToken: ct);

            return new StorageObject(key, (long)(found.Size ?? 0), found.ContentType,
                found.UpdatedDateTimeOffset, found.ETag, found.StorageClass);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        // The SDK writes into a stream rather than handing one over, and a preview reads the front of
        // it anyway; a memory stream keeps the interface the same as the others.
        var buffer = new MemoryStream();
        await Client.DownloadObjectAsync(Target.Container, Target.KeyOf(key), buffer,
            cancellationToken: ct);

        buffer.Position = 0;
        return buffer;
    }

    public async Task WriteAsync(string key, Stream content, string? contentType, CancellationToken ct) =>
        await Client.UploadObjectAsync(Target.Container, Target.KeyOf(key), contentType, content,
            cancellationToken: ct);

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        try
        {
            await Client.DeleteObjectAsync(Target.Container, Target.KeyOf(key), cancellationToken: ct);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone is the outcome that was asked for.
        }
    }

    public string SqlUri(string key) => $"gs://{Target.Container}/{Target.KeyOf(key)}";

    public string? SecretStatement()
    {
        // DuckDB reaches Google through the S3 protocol, and that means HMAC keys. Without them there
        // is no secret worth making: browsing still works, a query says what is missing.
        if (Target.Option("hmac") is not { Length: > 0 } id
            || Target.Option("hmacsecret") is not { Length: > 0 } secret)
            return null;

        return "CREATE OR REPLACE SECRET wds_storage (TYPE gcs, "
             + $"KEY_ID '{Escape(id)}', SECRET '{Escape(secret)}')";
    }

    private string Relative(string key)
    {
        var prefix = Target.Prefix.Length == 0 ? "" : Target.Prefix.TrimEnd('/') + "/";
        var relative = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
        return relative.TrimEnd('/');
    }

    private static string Name(string key)
    {
        var trimmed = key.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
