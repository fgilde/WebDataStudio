using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace WebDataStudio.Server.Storage;

/// Anything with an S3 endpoint: AWS itself, MinIO, Cloudflare R2, Wasabi, Ceph. One SDK covers them
/// all — the difference is an endpoint and whether the bucket is in the host name or the path.
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _client;

    public StorageTarget Target { get; }

    public S3ObjectStore(StorageTarget target)
    {
        Target = target;

        var config = new AmazonS3Config();
        var region = target.Option("region");

        // ServiceURL and RegionEndpoint are mutually exclusive in the SDK: setting one clears the
        // other. Setting a fallback region "just in case" therefore threw the endpoint away and sent
        // every request to the real AWS, which answered that the access key does not exist — a very
        // confusing way to be told the endpoint is gone.
        if (target.Option("endpoint") is { Length: > 0 } endpoint)
        {
            config.ServiceURL = endpoint;
            // Anything that is not AWS: the bucket goes in the path, because a made-up host name
            // does not resolve.
            config.ForcePathStyle = true;
            // The signature still names a region; it just does not decide where to connect.
            config.AuthenticationRegion = region is { Length: > 0 } ? region : "us-east-1";
        }
        else
        {
            config.RegionEndpoint = region is { Length: > 0 }
                ? RegionEndpoint.GetBySystemName(region)
                : RegionEndpoint.USEast1;
        }

        _client = target.Option("access") is { Length: > 0 } access
                  && target.Option("secret") is { Length: > 0 } secret
            ? new AmazonS3Client(new BasicAWSCredentials(access, secret), config)
            // No keys: the instance role, the environment, or whatever the credential chain finds.
            : new AmazonS3Client(config);
    }

    public async Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct)
    {
        var key = Target.KeyOf(prefix);
        var request = new ListObjectsV2Request
        {
            BucketName = Target.Container,
            Prefix = key.Length == 0 ? "" : key.TrimEnd('/') + "/",
            // The delimiter is what turns a flat key space into folders.
            Delimiter = "/",
            MaxKeys = max,
            ContinuationToken = cursor,
        };

        ListObjectsV2Response response;

        try
        {
            response = await _client.ListObjectsV2Async(request, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchBucket")
        {
            throw new StorageContainerMissingException(Target.Container, e);
        }

        var entries = new List<StorageEntry>();

        foreach (var folder in response.CommonPrefixes ?? [])
            entries.Add(new StorageEntry(Name(folder), Relative(folder), true, null, null));

        foreach (var item in response.S3Objects ?? [])
        {
            // The prefix itself comes back as a zero-length object in some stores; it is not a file.
            if (item.Key.EndsWith('/')) continue;

            entries.Add(new StorageEntry(Name(item.Key), Relative(item.Key), false,
                item.Size, item.LastModified is { } modified
                    ? new DateTimeOffset(modified, TimeSpan.Zero)
                    : null));
        }

        return new StoragePage(entries,
            response.IsTruncated == true ? response.NextContinuationToken : null);
    }

    public async Task<StorageObject?> HeadAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(
                Target.Container, Target.KeyOf(key), ct);

            return new StorageObject(key, response.ContentLength, response.Headers.ContentType,
                new DateTimeOffset(response.LastModified ?? DateTime.UtcNow, TimeSpan.Zero),
                response.ETag, response.StorageClass?.Value);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var response = await _client.GetObjectAsync(Target.Container, Target.KeyOf(key), ct);
        return response.ResponseStream;
    }

    public async Task WriteAsync(string key, Stream content, string? contentType, CancellationToken ct) =>
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Target.Container,
            Key = Target.KeyOf(key),
            InputStream = content,
            ContentType = contentType,
        }, ct);

    // Payload signing stays on. Turning it off to spare the SDK a seek is refused over plain HTTP
    // anyway, which is exactly what a MinIO on a private network is — so an upload that cannot be
    // rewound is buffered by the caller instead.

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _client.DeleteObjectAsync(Target.Container, Target.KeyOf(key), ct);

    public string SqlUri(string key) => $"s3://{Target.Container}/{Target.KeyOf(key)}";

    public string? SecretStatement()
    {
        var parts = new List<string> { "TYPE s3" };

        if (Target.Option("access") is { Length: > 0 } access
            && Target.Option("secret") is { Length: > 0 } secret)
        {
            parts.Add($"KEY_ID '{Escape(access)}'");
            parts.Add($"SECRET '{Escape(secret)}'");
        }
        else
        {
            // DuckDB can find the same instance role or environment credentials the SDK does.
            parts.Add("PROVIDER credential_chain");
        }

        if (Target.Option("region") is { Length: > 0 } region) parts.Add($"REGION '{Escape(region)}'");

        if (Target.Option("endpoint") is { Length: > 0 } endpoint)
        {
            var uri = new Uri(endpoint);
            parts.Add($"ENDPOINT '{Escape(uri.Authority)}'");
            parts.Add($"URL_STYLE 'path'");
            if (uri.Scheme == "http") parts.Add("USE_SSL false");
        }

        return $"CREATE OR REPLACE SECRET wds_storage ({string.Join(", ", parts)})";
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

    public void Dispose() => _client.Dispose();
}
