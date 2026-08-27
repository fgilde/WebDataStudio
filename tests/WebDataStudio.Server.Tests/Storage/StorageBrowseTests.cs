using System.Net;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// A Parquet in a bucket, opened the way a table is: the same endpoint, the same sorting, the same
/// filter language, the same paging. Nothing above the driver knows the rows came from a file.
public class StorageBrowseTests : IAsyncLifetime
{
    private readonly MinioFixture _minio = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-browse").FullName;
    private const string Bucket = "wds-browse";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _minio.StartAsync();
        await _minio.CreateBucketAsync(Bucket, Ct);

        var parquet = Path.Combine(_dir, "people.parquet").Replace('\\', '/');

        // DuckDB writes the file locally; the upload is the store's job, which keeps this test
        // honest about the path a real file takes into a bucket.
        await using (var duck = new DuckDBConnection("Data Source=:memory:"))
        {
            await duck.OpenAsync(Ct);
            await using var command = duck.CreateCommand();
            command.CommandText =
                $"COPY (SELECT * FROM (VALUES ('ada', 36), ('grace', 45), ('alan', 41)) "
                + $"AS t(name, age)) TO '{parquet}' (FORMAT parquet)";
            await command.ExecuteNonQueryAsync(Ct);
        }

        using var store = new S3ObjectStore(StorageUrl.Parse(_minio.UrlFor(Bucket)));
        await using var content = File.OpenRead(parquet);
        await store.WriteAsync("exports/people.parquet", content, "application/octet-stream", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _minio.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_LAKE"] = _minio.UrlFor(Bucket),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private const string Ref = "StorageObject:wds-browse/exports/people.parquet";

    private static async Task<JsonElement> RowsAsync(HttpClient client, string id, string query)
    {
        var response = await client.GetAsync($"/api/data/{id}?ref={Ref}&{query}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        // The body says what the engine objected to; a bare status code would send the next person
        // reading this test looking in the wrong place.
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task A_parquet_in_a_bucket_browses_sorts_filters_and_pages()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var all = await RowsAsync(client, id, "sort=age&desc=true");
        var names = all.GetProperty("rows").EnumerateArray()
            .Select(r => r[0].GetString()).ToList();
        Assert.Equal(["grace", "alan", "ada"], names);

        // The filter language, unchanged: ">40" means what it means against a table.
        var filtered = await RowsAsync(client, id, "filterColumn=age&filter=%3E40&sort=name");
        Assert.Equal(["alan", "grace"], filtered.GetProperty("rows").EnumerateArray()
            .Select(r => r[0].GetString()));

        var page = await RowsAsync(client, id, "sort=name&limit=1&offset=1");
        Assert.Equal(["alan"], page.GetProperty("rows").EnumerateArray()
            .Select(r => r[0].GetString()));
    }

    [Fact]
    public async Task A_file_no_reader_understands_says_so_instead_of_failing_as_sql()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var store = new S3ObjectStore(StorageUrl.Parse(_minio.UrlFor(Bucket)));
        await using (var content = new MemoryStream([0x50, 0x4b, 0x03, 0x04]))
            await store.WriteAsync("exports/notes.zip", content, "application/zip", Ct);

        var response = await client.GetAsync(
            $"/api/data/{id}?ref=StorageObject:{Bucket}/exports/notes.zip", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("preview", await response.Content.ReadAsStringAsync(Ct));
    }
}
