using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace WebDataStudio.Server.Tests.Storage;

/// A MinIO, started by hand rather than through the module for it.
///
/// The module sets the credentials through `MINIO_ACCESS_KEY`, which recent MinIO releases no longer
/// read — a container started that way keeps its own default root user and answers every request
/// with "that access key does not exist". Saying `MINIO_ROOT_USER` here makes the credentials the
/// test's business instead of a coincidence between two version numbers.
public sealed class MinioFixture : IAsyncDisposable
{
    public const string AccessKey = "wds-tests";
    public const string SecretKey = "wds-tests-secret";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("minio/minio:RELEASE.2025-04-22T22-12-26Z")
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithCommand("server", "/data")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
            request.ForPath("/minio/health/live").ForPort(9000)))
        .Build();

    public string Endpoint => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";

    public Task StartAsync() => _container.StartAsync();

    /// The connection URL for a bucket in this MinIO, in the form a `WDS_CONN_*` would carry.
    public string UrlFor(string bucket, string prefix = "") =>
        $"s3://{bucket}{(prefix.Length > 0 ? "/" + prefix : "")}"
        + $"?endpoint={Uri.EscapeDataString(Endpoint)}&access={AccessKey}&secret={SecretKey}"
        + "&region=us-east-1";

    /// Creates a bucket. The store deliberately does not do this: making a bucket is an act of
    /// administration, not of browsing one.
    public async Task CreateBucketAsync(string bucket, CancellationToken ct)
    {
        // No RegionEndpoint here: it and ServiceURL are mutually exclusive, and setting it last
        // would clear the endpoint.
        var config = new Amazon.S3.AmazonS3Config
        {
            ServiceURL = Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };

        using var client = new Amazon.S3.AmazonS3Client(AccessKey, SecretKey, config);
        await client.PutBucketAsync(bucket, ct);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
