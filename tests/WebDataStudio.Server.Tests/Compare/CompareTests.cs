using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Compare;
using WebDataStudio.Server.Ddl;

namespace WebDataStudio.Server.Tests.Compare;

public class SchemaComparerTests
{
    private static ColumnDefinition Column(string name, string type = "text") =>
        new(name, type, true, null, false, null);

    private static TableDefinition Table(string name, params ColumnDefinition[] columns) =>
        new("public", name, columns, [], [], null);

    [Fact]
    public void Identical_schemas_compare_equal()
    {
        var schema = new[] { Table("people", Column("id")) };
        var comparison = SchemaComparer.Compare(schema, schema);

        Assert.Empty(comparison.TablesOnlyInSource);
        Assert.Empty(comparison.TablesOnlyInTarget);
        Assert.Empty(comparison.ChangedTables);
        Assert.Single(comparison.IdenticalTables);
    }

    [Fact]
    public void A_table_only_in_the_source_is_reported_and_created()
    {
        var source = new[] { Table("people", Column("id")), Table("orders", Column("id")) };
        var target = new[] { Table("people", Column("id")) };

        var comparison = SchemaComparer.Compare(source, target);
        Assert.Equal("orders", Assert.Single(comparison.TablesOnlyInSource));

        var script = SchemaComparer.SyncScript(comparison, source, target, new PostgreSqlDdlWriter());
        Assert.Contains(script, s => s.Sql.Contains("CREATE TABLE") && s.Sql.Contains("orders"));
    }

    [Fact]
    public void A_changed_column_produces_an_alter()
    {
        var source = new[] { Table("people", Column("id"), Column("name", "int")) };
        var target = new[] { Table("people", Column("id"), Column("name", "text")) };

        var comparison = SchemaComparer.Compare(source, target);
        Assert.Single(comparison.ChangedTables);

        var script = SchemaComparer.SyncScript(comparison, source, target, new PostgreSqlDdlWriter());
        Assert.Contains(script, s => s.Sql.Contains("ALTER"));
    }

    [Fact]
    public void A_table_only_in_the_target_is_dropped_and_marked_destructive()
    {
        var source = new[] { Table("people", Column("id")) };
        var target = new[] { Table("people", Column("id")), Table("stale", Column("id")) };

        var comparison = SchemaComparer.Compare(source, target);
        Assert.Equal("stale", Assert.Single(comparison.TablesOnlyInTarget));

        var script = SchemaComparer.SyncScript(comparison, source, target, new PostgreSqlDdlWriter());
        Assert.Contains(script, s => s.Destructive && s.Sql.Contains("DROP TABLE"));
    }

    [Fact]
    public void The_script_is_written_in_the_target_dialect()
    {
        var source = new[] { Table("people", Column("id")) };
        var target = Array.Empty<TableDefinition>();

        var comparison = SchemaComparer.Compare(source, target);
        var script = SchemaComparer.SyncScript(comparison, source, target, new SqlServerDdlWriter());

        Assert.Contains(script, s => s.Sql.Contains("[people]"));
    }

    [Fact]
    public void Column_order_alone_is_not_a_difference()
    {
        var source = new[] { Table("people", Column("id"), Column("name")) };
        var target = new[] { Table("people", Column("name"), Column("id")) };

        Assert.Empty(SchemaComparer.Compare(source, target).ChangedTables);
    }
}

public class DataCompareEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-compare").FullName;
    private string _source = "";
    private string _target = "";

    public async ValueTask InitializeAsync()
    {
        _source = Path.Combine(_dir, "source.db");
        _target = Path.Combine(_dir, "target.db");

        await Seed(_source, """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people VALUES (1,'ada'),(2,'linus'),(3,'grace');
            CREATE TABLE only_here (id INTEGER PRIMARY KEY);
            """);

        await Seed(_target, """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people VALUES (1,'ada'),(2,'linus-changed'),(4,'extra');
            """);

        static async Task Seed(string path, string sql)
        {
            await using var db = new SqliteConnection($"Data Source={path}");
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SRC"] = $"sqlite:///{_source.Replace('\\', '/')}",
                ["WDS_CONN_DST"] = $"sqlite:///{_target.Replace('\\', '/')}",
            })));

    private static async Task<Dictionary<string, string>> IdsAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task Compares_two_schemas()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var body = await (await client.PostAsJsonAsync("/api/compare/schema", new
        {
            sourceConnectionId = ids["SRC"], targetConnectionId = ids["DST"],
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Contains("only_here",
            body.GetProperty("tablesOnlyInSource").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("CREATE TABLE", body.GetProperty("script").GetString());
    }

    [Fact]
    public async Task Compares_data_and_writes_a_sync_script()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var body = await (await client.PostAsJsonAsync("/api/compare/data", new
        {
            sourceConnectionId = ids["SRC"], sourceRef = "Table:main/people",
            targetConnectionId = ids["DST"], targetRef = "Table:main/people",
            keyColumns = new[] { "id" },
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        // id 3 is missing in the target, id 4 is extra there, id 2 differs, id 1 is identical.
        Assert.Equal(1, body.GetProperty("missing").GetArrayLength());
        Assert.Equal(1, body.GetProperty("extra").GetArrayLength());
        Assert.Equal(1, body.GetProperty("different").GetArrayLength());
        Assert.Equal(1, body.GetProperty("identical").GetInt32());

        var script = body.GetProperty("script").GetString()!;
        Assert.Contains("INSERT INTO", script);
        Assert.Contains("UPDATE", script);
        Assert.Contains("DELETE FROM", script);
    }

    [Fact]
    public async Task Data_comparison_names_the_changed_column()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var ids = await IdsAsync(client);

        var body = await (await client.PostAsJsonAsync("/api/compare/data", new
        {
            sourceConnectionId = ids["SRC"], sourceRef = "Table:main/people",
            targetConnectionId = ids["DST"], targetRef = "Table:main/people",
            keyColumns = Array.Empty<string>(),
        }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        // With no key columns given, the primary key is used.
        Assert.Equal("id", body.GetProperty("keyColumns")[0].GetString());
        Assert.Equal("name",
            body.GetProperty("different")[0].GetProperty("changedColumns")[0].GetString());
    }
}
