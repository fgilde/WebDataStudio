using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// S3 against MinIO, which is what the SDK talks to everywhere that is not AWS: an endpoint, path
/// style, and otherwise the same protocol.
public class S3ObjectStoreTests : ObjectStoreContract, IAsyncLifetime
{
    private readonly MinioFixture _minio = new();
    private S3ObjectStore? _store;

    protected override IObjectStore Store => _store!;

    public async ValueTask InitializeAsync()
    {
        await _minio.StartAsync();
        await _minio.CreateBucketAsync("lake", TestContext.Current.CancellationToken);

        _store = new S3ObjectStore(StorageUrl.Parse(_minio.UrlFor("lake")));
    }

    public async ValueTask DisposeAsync()
    {
        _store?.Dispose();
        await _minio.DisposeAsync();
    }

    [Fact]
    public void The_uri_is_the_one_duckdb_reads() =>
        Assert.Equal("s3://lake/exports/2026/a.csv", Store.SqlUri("exports/2026/a.csv"));

    [Fact]
    public void The_secret_carries_the_endpoint_and_the_path_style()
    {
        var secret = Store.SecretStatement();

        Assert.Contains("TYPE s3", secret);
        Assert.Contains("KEY_ID", secret);
        // MinIO over http, addressed by path: both have to be said, or DuckDB tries AWS over TLS.
        Assert.Contains("URL_STYLE 'path'", secret);
        Assert.Contains("USE_SSL false", secret);
    }

    [Fact]
    public void Without_keys_duckdb_is_told_to_find_its_own()
    {
        using var store = new S3ObjectStore(StorageUrl.Parse("s3://lake?region=eu-central-1"));

        Assert.Contains("PROVIDER credential_chain", store.SecretStatement());
        Assert.DoesNotContain("KEY_ID", store.SecretStatement());
    }

    [Fact]
    public async Task A_prefix_scoped_connection_only_sees_its_own_folder()
    {
        await SeedAsync();

        // The same bucket, opened at exports/2026: the root object is not in it.
        using var scoped = new S3ObjectStore(StorageUrl.Parse(_minio.UrlFor("lake", "exports/2026")));
        var page = await scoped.ListAsync("", null, 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Entries.Count);
        Assert.DoesNotContain(page.Entries, entry => entry.Name == "root.txt");
        Assert.Equal("s3://lake/exports/2026/a.csv", scoped.SqlUri("a.csv"));
    }
}
