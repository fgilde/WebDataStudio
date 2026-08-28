using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// The object rather than its rows: preview, download, upload, delete — and the two connections that
/// refuse to be written to.
public class StorageEndpointTests : IAsyncLifetime
{
    private readonly MinioFixture _minio = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-storage-api").FullName;
    private const string Bucket = "wds-api";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _minio.StartAsync();
        await _minio.CreateBucketAsync(Bucket, Ct);

        using var store = new S3ObjectStore(StorageUrl.Parse(_minio.UrlFor(Bucket)));

        await using (var text = new MemoryStream(Encoding.UTF8.GetBytes("name,age\nada,36\n")))
            await store.WriteAsync("exports/people.csv", text, "text/csv", Ct);

        // A PNG header: nothing about it should come back as text.
        await using (var image = new MemoryStream([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 1]))
            await store.WriteAsync("exports/logo.png", image, "image/png", Ct);

        // Two more files under the same prefix, and one a folder deeper: a zip of the prefix has to
        // hold all of them, with the folder inside it.
        await using (var more = new MemoryStream(Encoding.UTF8.GetBytes("id,total\n1,10\n")))
            await store.WriteAsync("exports/orders.csv", more, "text/csv", Ct);

        await using (var nested = new MemoryStream(Encoding.UTF8.GetBytes("month,orders\n06,12\n")))
            await store.WriteAsync("exports/monthly/2026-06.csv", nested, "text/csv", Ct);

        // A PDF is ASCII near the front, so the NUL test alone called it text and offered its object
        // headers as a preview.
        await using (var pdf = new MemoryStream(Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Size 1 >>\n%%EOF\n")))
            await store.WriteAsync("docs/handbook.pdf", pdf, "application/pdf", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _minio.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(
        bool readOnly = false, string? color = null, int? previewBytes = null,
        int? archiveMaxObjects = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir,
                    $"wds-{readOnly}-{color}-{previewBytes}-{archiveMaxObjects}.db"),
                ["WDS_STORAGE_ARCHIVE_MAX_OBJECTS"] = archiveMaxObjects?.ToString(),
                ["WDS_CONN_LAKE"] = _minio.UrlFor(Bucket),
                ["WDS_CONN_LAKE_READONLY"] = readOnly ? "true" : null,
                ["WDS_CONN_LAKE_COLOR"] = color,
                ["WDS_STORAGE_PREVIEW_BYTES"] = previewBytes?.ToString(),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private const string Csv = $"StorageObject:{Bucket}/exports/people.csv";

    [Fact]
    public async Task A_preview_gives_the_text_and_the_object_s_own_facts()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var preview = JsonDocument.Parse(
            await client.GetStringAsync($"/api/storage/{id}/preview?ref={Csv}", Ct)).RootElement;

        Assert.Equal("name,age\nada,36\n", preview.GetProperty("text").GetString());
        Assert.Equal("text/csv", preview.GetProperty("contentType").GetString());
        Assert.Equal(16, preview.GetProperty("size").GetInt64());
        Assert.False(preview.GetProperty("truncated").GetBoolean());
        // A CSV is a table as well, and the UI offers that rather than only the text.
        Assert.True(preview.GetProperty("queryable").GetBoolean());
    }

    [Fact]
    public async Task A_preview_reads_the_front_of_a_file_and_says_that_it_did()
    {
        using var factory = Factory(previewBytes: 8);
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var preview = JsonDocument.Parse(
            await client.GetStringAsync($"/api/storage/{id}/preview?ref={Csv}", Ct)).RootElement;

        Assert.Equal("name,age", preview.GetProperty("text").GetString());
        Assert.True(preview.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task An_image_is_not_offered_as_text()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var preview = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/storage/{id}/preview?ref=StorageObject:{Bucket}/exports/logo.png", Ct)).RootElement;

        Assert.True(preview.GetProperty("binary").GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("text").ValueKind);
        Assert.False(preview.GetProperty("queryable").GetBoolean());
    }

    [Fact]
    public async Task A_download_streams_the_object_under_its_own_name()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.GetAsync($"/api/storage/{id}/download?ref={Csv}", Ct);
        response.EnsureSuccessStatusCode();

        Assert.Equal("name,age\nada,36\n", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("people.csv", response.Content.Headers.ContentDisposition?.FileNameStar
                                   ?? response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task A_pdf_is_shown_rather_than_read_as_text()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var preview = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/storage/{id}/preview?ref=StorageObject:{Bucket}/docs/handbook.pdf", Ct)).RootElement;

        Assert.True(preview.GetProperty("binary").GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("text").ValueKind);
        Assert.Equal("application/pdf", preview.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task And_the_same_bytes_come_without_the_attachment_header_to_be_shown()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.GetAsync($"/api/storage/{id}/download?ref={Csv}&inline=true", Ct);
        response.EnsureSuccessStatusCode();

        Assert.Equal("name,age\nada,36\n", await response.Content.ReadAsStringAsync(Ct));
        // A PDF, a video or a recording behind an attachment header lands in the downloads folder
        // instead of on screen, which is the whole difference between the two.
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_whole_prefix_comes_back_as_one_zip()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.GetAsync(
            $"/api/storage/{id}/archive?ref=Prefix:{Bucket}/exports", Ct);
        response.EnsureSuccessStatusCode();

        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("exports.zip", response.Content.Headers.ContentDisposition?.FileNameStar
                                    ?? response.Content.Headers.ContentDisposition?.FileName);

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(Ct));
        var names = zip.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToList();

        // The paths are relative to the prefix that was asked for: unzipping gives back the folder,
        // not the whole bucket's key space.
        Assert.Contains("people.csv", names);
        Assert.Contains("orders.csv", names);
        Assert.Contains("monthly/2026-06.csv", names);

        using var reader = new StreamReader(
            zip.Entries.First(entry => entry.FullName == "people.csv").Open());
        Assert.Equal("name,age\nada,36\n", await reader.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task A_zip_that_had_to_stop_says_so_inside_itself()
    {
        // One object allowed: whatever stops the walk is written into the zip, because a response
        // that is already streaming cannot go back and become an error.
        using var factory = Factory(archiveMaxObjects: 1);
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.GetAsync(
            $"/api/storage/{id}/archive?ref=Prefix:{Bucket}/exports", Ct);
        response.EnsureSuccessStatusCode();

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(Ct));

        Assert.Equal(2, zip.Entries.Count);
        var note = zip.Entries.First(entry => entry.FullName == "TRUNCATED.txt");

        using var reader = new StreamReader(note.Open());
        Assert.Contains("WDS_STORAGE_ARCHIVE_MAX_OBJECTS", await reader.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task An_upload_lands_in_the_folder_that_was_open_and_can_be_deleted_again()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var content = new StringContent("hello", Encoding.UTF8, "text/plain");
        var upload = await client.PostAsync(
            $"/api/storage/{id}/upload?ref=Prefix:{Bucket}/exports&name=note.txt", content, Ct);
        upload.EnsureSuccessStatusCode();

        var landed = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/storage/{id}/preview?ref=StorageObject:{Bucket}/exports/note.txt", Ct)).RootElement;
        Assert.Equal("hello", landed.GetProperty("text").GetString());

        var deleted = await client.DeleteAsync(
            $"/api/storage/{id}?ref=StorageObject:{Bucket}/exports/note.txt", Ct);
        deleted.EnsureSuccessStatusCode();

        var gone = await client.GetAsync(
            $"/api/storage/{id}/preview?ref=StorageObject:{Bucket}/exports/note.txt", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task A_name_that_tries_to_point_somewhere_else_is_refused()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var content = new StringContent("x");
        var response = await client.PostAsync(
            $"/api/storage/{id}/upload?ref=Prefix:{Bucket}/exports&name=../escaped.txt", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_read_only_connection_refuses_to_be_written_to()
    {
        using var factory = Factory(readOnly: true);
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var content = new StringContent("x");
        var upload = await client.PostAsync(
            $"/api/storage/{id}/upload?ref=Prefix:{Bucket}/exports&name=note.txt", content, Ct);
        var delete = await client.DeleteAsync($"/api/storage/{id}?ref={Csv}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        // Reading is untouched: the refusal is about changing things.
        (await client.GetAsync($"/api/storage/{id}/preview?ref={Csv}", Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_production_connection_refuses_as_well()
    {
        using var factory = Factory(color: "red");
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var delete = await client.DeleteAsync($"/api/storage/{id}?ref={Csv}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Contains("production", await delete.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Deleting_a_folder_is_not_something_this_offers()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.DeleteAsync($"/api/storage/{id}?ref=Prefix:{Bucket}/exports", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Testing_a_storage_connection_actually_reaches_the_bucket()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/connections/test", new
        {
            name = "LAKE", engine = "storage", connectionString = _minio.UrlFor(Bucket),
            readOnly = false,
        }, Ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        // Opening a storage connection builds a client and a DuckDB and touches nothing: a probe that
        // stopped there would say "connected" for a bucket that does not exist.
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Contains("object(s)", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task And_says_so_when_the_bucket_is_not_there()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/connections/test", new
        {
            name = "NOPE", engine = "storage",
            connectionString = _minio.UrlFor("no-such-bucket"), readOnly = false,
        }, Ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(result.GetProperty("message").GetString() ?? "");
    }
}
