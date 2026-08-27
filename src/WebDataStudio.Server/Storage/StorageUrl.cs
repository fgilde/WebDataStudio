
namespace WebDataStudio.Server.Storage;

/// Which object store a connection points at.
public enum StorageProvider { S3, AzureBlob, GoogleCloud, Local }

/// A storage connection, taken apart. `Container` is the bucket or the blob container; `Prefix` is
/// the part of the key space this connection is limited to, so a connection can hand somebody one
/// folder of a bucket rather than the whole thing.
public sealed record StorageTarget(
    StorageProvider Provider,
    string? Account,
    string Container,
    string Prefix,
    IReadOnlyDictionary<string, string> Options)
{
    public string? Option(string name) => Options.TryGetValue(name, out var value) ? value : null;

    /// True when the connection carries credentials of its own. Without them the machine's identity
    /// is used — a managed identity on Azure, an instance role on AWS, application default
    /// credentials on Google.
    public bool HasExplicitCredentials =>
        Option("key") is not null || Option("sas") is not null
        || (Option("access") is not null && Option("secret") is not null)
        || (Option("hmac") is not null && Option("hmacsecret") is not null);

    /// The key a prefix-scoped connection actually addresses.
    public string KeyOf(string relative) =>
        Prefix.Length == 0 ? relative.TrimStart('/') : $"{Prefix.TrimEnd('/')}/{relative.TrimStart('/')}";
}

/// Reads the URL form a storage connection is configured with.
///
///   s3://bucket/prefix?region=eu-central-1&amp;endpoint=https://minio:9000&amp;access=…&amp;secret=…
///   azblob://account/container/prefix?key=…            (or ?sas=…)
///   gs://bucket/prefix?hmac=…&amp;hmacsecret=…
///   file:///data/incoming
///
/// One engine id — `storage` — and the scheme decides the provider. Credentials are optional
/// everywhere: without them the machine's own identity is used.
public static class StorageUrl
{
    public static bool IsStorageScheme(string scheme) => Provider(scheme) is not null;

    private static StorageProvider? Provider(string scheme) => scheme.ToLowerInvariant() switch
    {
        "s3" => StorageProvider.S3,
        "azblob" or "azure" => StorageProvider.AzureBlob,
        "gs" or "gcs" => StorageProvider.GoogleCloud,
        "file" => StorageProvider.Local,
        _ => null,
    };

    public static StorageTarget Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new FormatException($"'{url}' is not a storage URL");

        var provider = Provider(uri.Scheme)
            ?? throw new FormatException($"'{uri.Scheme}' is not a storage scheme");

        var options = Options(uri);

        // A file URL is a path, not a host and a container. Everything after the root is the prefix,
        // and the "container" is the root itself so the rest of the code has one shape to work with.
        if (provider == StorageProvider.Local)
        {
            var path = Uri.UnescapeDataString(uri.LocalPath);
            if (path.Length == 0) throw new FormatException("a file:// storage connection needs a path");

            return new StorageTarget(provider, null, path, "", options);
        }

        // s3://bucket/prefix — the host is the bucket. azblob://account/container/prefix — the host
        // is the account and the first path segment is the container, because a storage account has
        // more than one and the connection has to say which.
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (provider == StorageProvider.AzureBlob)
        {
            // azblob:///container?connectionstring=... — an app host hands over a connection string
            // or a service URI, and the account name is already inside it. Asking for it a second
            // time in the URL would be a way to write down two different accounts.
            var account = uri.Host.Length > 0 ? uri.Host : null;

            if (account is null && options.GetValueOrDefault("connectionstring") is not { Length: > 0 })
                throw new FormatException(
                    "an azblob:// connection needs an account: azblob://account/container");

            if (segments.Length == 0)
                throw new FormatException("an azblob:// connection needs a container: azblob://account/container");

            return new StorageTarget(provider, account, segments[0],
                string.Join('/', segments.Skip(1)), options);
        }

        if (uri.Host.Length == 0) throw new FormatException($"'{url}' names no bucket");

        return new StorageTarget(provider, null, uri.Host, string.Join('/', segments), options);
    }

    private static Dictionary<string, string> Options(Uri uri)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            // Split on the first '=' only: an Azure connection string is itself full of them.
            var split = pair.Split('=', 2);

            // Unescaped as a URI component, not as a query string: a query parser turns '+' into a
            // space, and a base64 account key is full of them — which broke the key silently.
            options[Uri.UnescapeDataString(split[0])] =
                split.Length > 1 ? Uri.UnescapeDataString(split[1]) : "";
        }

        return options;
    }
}
