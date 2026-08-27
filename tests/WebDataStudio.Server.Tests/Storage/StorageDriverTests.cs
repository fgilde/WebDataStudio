using DuckDB.NET.Data;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.Storage;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// The driver that makes a bucket a connection: the tree comes from the store, the SQL from the
/// DuckDB the session holds. The remote half runs against MinIO; the paging and the extension
/// preamble are checked where they can be checked without one.
public class StorageDriverTests : IAsyncLifetime
{
    private readonly MinioFixture _minio = new();
    private readonly StorageDriver _driver = new();
    private const string Bucket = "wds-driver";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _minio.StartAsync();
        await _minio.CreateBucketAsync(Bucket, Ct);

        using var store = new S3ObjectStore(StorageUrl.Parse(_minio.UrlFor(Bucket)));
        await PutAsync(store, "root.txt", "at the root");
        await PutAsync(store, "exports/people.csv", "name,age\nada,36\ngrace,45\n");
        await PutAsync(store, "exports/notes.zip", "not a table");
    }

    public async ValueTask DisposeAsync() => await _minio.DisposeAsync();

    private static async Task PutAsync(IObjectStore store, string key, string content)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await store.WriteAsync(key, stream, "text/plain", Ct);
    }

    private async Task<IDbSession> OpenAsync(string? url = null) =>
        await _driver.OpenAsync(new ConnectionSpec("t", "lake", "storage",
            url ?? _minio.UrlFor(Bucket), false, null, null, ConnectionSource.Environment), Ct);

    [Fact]
    public async Task The_root_of_the_tree_is_the_container_the_connection_points_at()
    {
        await using var session = await OpenAsync();

        var nodes = await _driver.IntrospectAsync(session, null, Ct);

        var container = Assert.Single(nodes);
        Assert.Equal(SchemaNodeKind.Container, container.Ref.Kind);
        Assert.Equal(Bucket, container.Label);
    }

    [Fact]
    public async Task A_container_lists_its_prefixes_and_its_objects()
    {
        await using var session = await OpenAsync();
        var root = (await _driver.IntrospectAsync(session, null, Ct))[0];

        var nodes = await _driver.IntrospectAsync(session, root.Ref, Ct);

        Assert.Contains(nodes, n => n.Ref.Kind == SchemaNodeKind.Prefix && n.Label == "exports");
        var file = Assert.Single(nodes, n => n.Label == "root.txt");
        Assert.Equal(SchemaNodeKind.StorageObject, file.Ref.Kind);
        // The row says how big it is and when it landed, which is why somebody opened the tree.
        Assert.Contains("11 B", file.Detail);
    }

    [Fact]
    public async Task A_prefix_lists_what_is_inside_it_and_nothing_from_above()
    {
        await using var session = await OpenAsync();
        var prefix = new SchemaNodeRef(SchemaNodeKind.Prefix, [Bucket, "exports"]);

        var nodes = await _driver.IntrospectAsync(session, prefix, Ct);

        Assert.Equal(2, nodes.Count);
        Assert.DoesNotContain(nodes, n => n.Label == "root.txt");
    }

    [Fact]
    public async Task An_object_reports_its_columns_and_its_own_facts()
    {
        await using var session = await OpenAsync();
        var target = new SchemaNodeRef(SchemaNodeKind.StorageObject, [Bucket, "exports", "people.csv"]);

        var detail = await _driver.DescribeAsync(session, target, Ct);

        Assert.Equal(["name", "age"], detail.Columns.Select(c => c.Name));
        Assert.Equal(25, detail.SizeBytes);
        Assert.Contains("text/plain", detail.Comment);
    }

    [Fact]
    public async Task A_csv_in_a_bucket_is_queried_like_a_table()
    {
        await using var session = await OpenAsync();
        var target = new SchemaNodeRef(SchemaNodeKind.StorageObject, [Bucket, "exports", "people.csv"]);

        var from = _driver.FromClause(session, target);
        Assert.StartsWith("read_csv_auto('s3://", from);

        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var chunk in _driver.ExecuteAsync(session,
            new ScriptRequest($"SELECT name FROM {from} ORDER BY age DESC", 100, 30), Ct))
        {
            Assert.IsNotType<ResultChunk.Error>(chunk);
            if (chunk is ResultChunk.Rows page) rows.AddRange(page.Items);
        }

        Assert.Equal(["grace", "ada"], rows.Select(r => r[0]?.ToString()));
    }

    [Fact]
    public async Task Something_no_reader_understands_has_no_from_clause()
    {
        await using var session = await OpenAsync();

        Assert.Null(_driver.FromClause(session,
            new SchemaNodeRef(SchemaNodeKind.StorageObject, [Bucket, "exports", "notes.zip"])));
    }

    [Fact]
    public async Task A_folder_is_a_table_only_once_a_pattern_says_which_files()
    {
        await using var session = await OpenAsync();

        // Without one, guessing a format would turn a folder of CSVs into a Parquet error.
        Assert.Null(_driver.FromClause(session,
            new SchemaNodeRef(SchemaNodeKind.Prefix, [Bucket, "exports"])));

        Assert.StartsWith("read_csv_auto(", _driver.FromClause(session,
            new SchemaNodeRef(SchemaNodeKind.Prefix, [Bucket, "exports", "*.csv"])));
    }

    [Fact]
    public void The_capabilities_say_what_a_file_cannot_do()
    {
        Assert.True(_driver.Caps.Sql);
        Assert.True(_driver.Caps.TabularBrowse);
        // No DDL, no transactions, no schemas: a file has none of them, and the UI hides what a
        // driver says false to.
        Assert.False(_driver.Caps.Ddl);
        Assert.False(_driver.Caps.Transactions);
        Assert.False(_driver.Caps.MultiSchema);
    }
}

/// The parts that need no container: which statements a session runs before its first query, and
/// that a listing is paged rather than walked.
public class StoragePreambleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_folder_needs_no_extension_at_all() =>
        Assert.Empty(DuckDbExtensions.Preamble(StorageProvider.Local, "/opt/duckdb/extensions"));

    [Fact]
    public void With_a_bundle_nothing_reaches_for_the_network()
    {
        var statements = DuckDbExtensions.Preamble(StorageProvider.S3, "/opt/duckdb/extensions");

        Assert.Equal([
            "SET extension_directory='/opt/duckdb/extensions'",
            "SET autoinstall_known_extensions=false",
            "SET autoload_known_extensions=false",
            "LOAD httpfs",
        ], statements);
    }

    [Fact]
    public void Azure_needs_its_own_extension_as_well() =>
        Assert.Contains("LOAD azure",
            DuckDbExtensions.Preamble(StorageProvider.AzureBlob, "/opt/duckdb/extensions"));

    [Fact]
    public void Without_a_bundle_the_session_installs_what_it_needs()
    {
        var statements = DuckDbExtensions.Preamble(StorageProvider.S3, null);

        Assert.Equal(["INSTALL httpfs", "LOAD httpfs"], statements);
    }

    [Fact]
    public async Task An_extension_loads_from_a_staged_directory_with_auto_install_off()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wds-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // Staging is what the image build does; here one install fills the directory.
            await using (var staging = new DuckDBConnection("Data Source=:memory:"))
            {
                await staging.OpenAsync(Ct);
                try
                {
                    await RunAsync(staging, $"SET extension_directory='{directory}'");
                    await RunAsync(staging, "INSTALL httpfs");
                }
                catch (DuckDBException e)
                {
                    Assert.Skip($"no network to stage the extension from: {e.Message}");
                }
            }

            await using var session = new DuckDBConnection("Data Source=:memory:");
            await session.OpenAsync(Ct);

            foreach (var statement in DuckDbExtensions.Preamble(StorageProvider.S3, directory))
                await RunAsync(session, statement);

            await using var command = session.CreateCommand();
            command.CommandText =
                "SELECT installed, loaded FROM duckdb_extensions() WHERE extension_name = 'httpfs'";

            await using var reader = await command.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }
        finally
        {
            // The loaded extension stays mapped into this process on Windows, so the file cannot be
            // removed while the test host lives. Temp is the right place for it either way.
            try { Directory.Delete(directory, true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task A_listing_longer_than_a_page_offers_a_row_that_fetches_the_rest()
    {
        var root = Path.Combine(Path.GetTempPath(), "wds-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "many"));

        try
        {
            // One more than a page: the tree must not walk the rest on its own.
            for (var i = 0; i < 501; i++)
                await File.WriteAllTextAsync(Path.Combine(root, "many", $"f{i:000}.txt"), "x", Ct);

            var driver = new StorageDriver();
            await using var session = await driver.OpenAsync(new ConnectionSpec("t", "drop", "storage",
                new Uri(root).AbsoluteUri, false, null, null, ConnectionSource.Environment), Ct);

            var container = (await driver.IntrospectAsync(session, null, Ct))[0];
            var folder = (await driver.IntrospectAsync(session, container.Ref, Ct))
                .Single(n => n.Label == "many");

            var first = await driver.IntrospectAsync(session, folder.Ref, Ct);
            var more = Assert.Single(first, n => n.Ref.Kind == SchemaNodeKind.StorageMore);
            Assert.Equal(501, first.Count);

            var rest = await driver.IntrospectAsync(session, more.Ref, Ct);

            Assert.Single(rest);
            Assert.DoesNotContain(rest, n => n.Ref.Kind == SchemaNodeKind.StorageMore);
            Assert.NotEqual(first[0].Label, rest[0].Label);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task RunAsync(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
