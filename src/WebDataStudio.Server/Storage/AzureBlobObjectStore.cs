using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace WebDataStudio.Server.Storage;

/// Azure Blob Storage, and Azurite, which is the same thing on a laptop.
///
/// Three ways in, in the order a deployment should prefer them: the machine's own managed identity,
/// a shared-access signature, an account key.
public sealed class AzureBlobObjectStore : IObjectStore
{
    private readonly BlobContainerClient _container;

    public StorageTarget Target { get; }

    public AzureBlobObjectStore(StorageTarget target)
    {
        Target = target;
        _container = Connect(target);
    }

    private static BlobContainerClient Connect(StorageTarget target)
    {
        if (target.Option("connectionstring") is { Length: > 0 } connectionString)
        {
            // An Aspire blob resource hands over a connection string while developing (Azurite) and
            // the service URI itself once deployed. A URI means the identity this process runs as,
            // which is the whole point of not carrying a key.
            if (connectionString.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return new BlobContainerClient(
                    new Uri($"{connectionString.TrimEnd('/')}/{target.Container}"),
                    new DefaultAzureCredential());

            return new BlobServiceClient(connectionString).GetBlobContainerClient(target.Container);
        }

        var account = target.Account ?? throw new FormatException("an azblob:// URL needs an account");

        // Azurite is not at *.blob.core.windows.net, so a connection to it says where it is.
        var endpoint = target.Option("endpoint") is { Length: > 0 } given
            ? given.TrimEnd('/')
            : $"https://{account}.blob.core.windows.net";

        if (target.Option("sas") is { Length: > 0 } sas)
            return new BlobContainerClient(
                new Uri($"{endpoint}/{target.Container}?{sas.TrimStart('?')}"));

        if (target.Option("key") is { Length: > 0 } key)
            return new BlobContainerClient(new Uri($"{endpoint}/{target.Container}"),
                new StorageSharedKeyCredential(account, key));

        // Nothing given: the identity this process runs as. In Azure that is the managed identity,
        // which is the whole reason not to carry a key around.
        return new BlobContainerClient(new Uri($"{endpoint}/{target.Container}"),
            new DefaultAzureCredential());
    }

    public async Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct)
    {
        var key = Target.KeyOf(prefix);
        var search = key.Length == 0 ? "" : key.TrimEnd('/') + "/";

        var entries = new List<StorageEntry>();
        string? next = null;

        // The delimiter is what makes a flat namespace look like folders; the SDK hands both back in
        // one hierarchy listing.
        var pages = _container
            .GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", search, ct)
            .AsPages(cursor, max);

        try
        {
        await foreach (var page in pages)
        {
            foreach (var item in page.Values)
            {
                if (item.IsPrefix && item.Prefix is { } folder)
                {
                    entries.Add(new StorageEntry(Name(folder), Relative(folder), true, null, null));
                    continue;
                }

                if (item.Blob is not { } blob) continue;

                entries.Add(new StorageEntry(Name(blob.Name), Relative(blob.Name), false,
                    blob.Properties.ContentLength, blob.Properties.LastModified));
            }

            next = page.ContinuationToken;
            // One page per call: the caller asked for `max` and gets it, with a cursor for the rest.
            break;
        }

        }
        catch (RequestFailedException e) when (e.ErrorCode == "ContainerNotFound")
        {
            throw new StorageContainerMissingException(Target.Container, e);
        }

        return new StoragePage(entries, string.IsNullOrEmpty(next) ? null : next);
    }

    public async Task<StorageObject?> HeadAsync(string key, CancellationToken ct)
    {
        try
        {
            var blob = _container.GetBlobClient(Target.KeyOf(key));
            var properties = await blob.GetPropertiesAsync(cancellationToken: ct);

            return new StorageObject(key, properties.Value.ContentLength,
                properties.Value.ContentType, properties.Value.LastModified,
                properties.Value.ETag.ToString(), properties.Value.AccessTier);
        }
        catch (RequestFailedException e) when (e.ErrorCode == "ContainerNotFound")
        {
            throw new StorageContainerMissingException(Target.Container, e);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // The container is there and this key is not, which is an ordinary answer.
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
        await _container.GetBlobClient(Target.KeyOf(key)).OpenReadAsync(cancellationToken: ct);

    public async Task WriteAsync(string key, Stream content, string? contentType, CancellationToken ct) =>
        await _container.GetBlobClient(Target.KeyOf(key)).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = contentType is { Length: > 0 }
                ? new BlobHttpHeaders { ContentType = contentType }
                : null,
        }, ct);

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _container.GetBlobClient(Target.KeyOf(key)).DeleteIfExistsAsync(cancellationToken: ct);

    /// DuckDB's azure extension addresses a blob as `az://container/key`; which account that is comes
    /// from the secret.
    public string SqlUri(string key) => $"az://{Target.Container}/{Target.KeyOf(key)}";

    public string? SecretStatement()
    {
        var account = Target.Account ?? "";

        if (Target.Option("connectionstring") is { Length: > 0 } connectionString)
        {
            // A service URI is not a connection string: DuckDB takes the account name and finds the
            // same credential chain the SDK does.
            if (connectionString.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return $"CREATE OR REPLACE SECRET wds_storage (TYPE azure, PROVIDER credential_chain, "
                     + $"ACCOUNT_NAME '{Escape(AccountOf(connectionString) ?? account)}')";

            return $"CREATE OR REPLACE SECRET wds_storage (TYPE azure, "
                 + $"CONNECTION_STRING '{Escape(connectionString)}')";
        }

        if (Target.Option("key") is { Length: > 0 } key)
        {
            var endpoint = Target.Option("endpoint") is { Length: > 0 } given
                ? $";BlobEndpoint={given.TrimEnd('/')}/{account}"
                : "";

            // The azure extension takes an account key as a connection string and nothing else.
            return $"CREATE OR REPLACE SECRET wds_storage (TYPE azure, CONNECTION_STRING "
                 + $"'DefaultEndpointsProtocol=https;AccountName={Escape(account)};"
                 + $"AccountKey={Escape(key)}{Escape(endpoint)}')";
        }

        // No key: the same credential chain the SDK walks, so a managed identity works here too.
        return $"CREATE OR REPLACE SECRET wds_storage (TYPE azure, PROVIDER credential_chain, "
             + $"ACCOUNT_NAME '{Escape(account)}')";
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

    /// The account name in a blob service URI: the first label of its host.
    private static string? AccountOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Host.Split('.') is [var first, ..]
            ? first
            : null;

    private static string Escape(string value) => value.Replace("'", "''");
}
