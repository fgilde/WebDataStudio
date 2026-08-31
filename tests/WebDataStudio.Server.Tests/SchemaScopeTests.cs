using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Which schemas a connection reads. A server with five thousand tables should not make every studio
/// pay for all of them, and empty still means everything.
public class SchemaScopeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-scope").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA sales;
            CREATE SCHEMA archive;
            CREATE TABLE sales.orders (id int);
            CREATE TABLE archive.orders_2019 (id int);
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(string? schemas = null, string suffix = "") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, $"wds{suffix}.db"),
                ["WDS_CONN_PG"] = _container.GetConnectionString(),
                ["WDS_CONN_PG_ENGINE"] = "postgresql",
                ["WDS_CONN_PG_SCHEMAS"] = schemas,
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    /// Only the schemas: PostgreSQL's root also carries the server-level folders — extensions,
    /// roles, tablespaces — and those are deliberately not filtered by a schema scope.
    private static async Task<List<string>> SchemasAsync(HttpClient client, string id) =>
        JsonDocument.Parse(await client.GetStringAsync($"/api/schema/{id}", Ct)).RootElement
            .EnumerateArray()
            .Where(node => node.GetProperty("kind").GetString() is "Schema" or "Database")
            .Select(node => node.GetProperty("label").GetString() ?? "")
            .ToList();

    [Fact]
    public async Task Without_a_scope_the_tree_shows_every_schema()
    {
        using var factory = Factory(suffix: "-all");
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var schemas = await SchemasAsync(client, id);

        Assert.Contains("public", schemas);
        Assert.Contains("sales", schemas);
        Assert.Contains("archive", schemas);
    }

    [Fact]
    public async Task The_environment_can_fix_which_schemas_are_read()
    {
        using var factory = Factory("public,sales", "-env");
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var schemas = await SchemasAsync(client, id);

        Assert.Equal(["public", "sales"], schemas.Order());
    }

    [Fact]
    public async Task And_then_the_studio_cannot_widen_it()
    {
        using var factory = Factory("public", "-fixed");
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var scope = JsonDocument.Parse(
            await client.GetStringAsync($"/api/schema/{id}/scope", Ct)).RootElement;

        Assert.False(scope.GetProperty("editable").GetBoolean());
        Assert.Equal(["public"], scope.GetProperty("fixedByEnvironment").EnumerateArray()
            .Select(entry => entry.GetString()));

        var refused = await client.PutAsJsonAsync($"/api/schema/{id}/scope", new[] { "sales" }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_studio_can_choose_a_scope_for_itself_and_the_tree_follows()
    {
        using var factory = Factory(suffix: "-chosen");
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var scope = JsonDocument.Parse(
            await client.GetStringAsync($"/api/schema/{id}/scope", Ct)).RootElement;

        Assert.True(scope.GetProperty("editable").GetBoolean());
        Assert.Contains("archive", scope.GetProperty("available").EnumerateArray()
            .Select(entry => entry.GetString()));

        (await client.PutAsJsonAsync($"/api/schema/{id}/scope", new[] { "sales" }, Ct))
            .EnsureSuccessStatusCode();

        var schemas = await SchemasAsync(client, id);

        Assert.Equal(["sales"], schemas);
    }

    /// The catalogue is there when somebody asks for it and not before: pg_catalog and
    /// information_schema hold hundreds of relations nobody in this database wrote.
    [Fact]
    public async Task System_schemas_stay_out_of_the_tree_until_the_connection_asks_for_them()
    {
        using var factory = Factory(suffix: "-system");
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var hidden = await SchemasAsync(client, id);

        Assert.DoesNotContain("pg_catalog", hidden);
        Assert.DoesNotContain("information_schema", hidden);

        (await client.PutAsync($"/api/schema/{id}/system?show=true", null, Ct))
            .EnsureSuccessStatusCode();

        var shown = await SchemasAsync(client, id);

        Assert.Contains("pg_catalog", shown);
        Assert.Contains("information_schema", shown);
        Assert.Contains("public", shown);

        // And the objects in them, which is the point of asking.
        var tables = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/schema/{id}?parent={Uri.EscapeDataString("TableFolder:pg_catalog/tables")}", Ct))
            .RootElement.EnumerateArray().Select(node => node.GetProperty("label").GetString()).ToList();

        Assert.Contains("pg_class", tables);

        (await client.PutAsync($"/api/schema/{id}/system?show=false", null, Ct))
            .EnsureSuccessStatusCode();

        Assert.DoesNotContain("pg_catalog", await SchemasAsync(client, id));
    }

    [Fact]
    public async Task A_scope_that_names_nothing_is_a_scope_of_everything()
    {
        using var factory = Factory(suffix: "-empty");
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        (await client.PutAsJsonAsync($"/api/schema/{id}/scope", new[] { "sales" }, Ct))
            .EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/schema/{id}/scope", Array.Empty<string>(), Ct))
            .EnsureSuccessStatusCode();

        Assert.Contains("archive", await SchemasAsync(client, id));
    }
}

/// The filtering itself, without a server: what passes through matters as much as what does not.
public class SchemaScopeFilterTests
{
    private static SchemaScope Scope(string? schemas, out WorkspaceStore workspace)
    {
        var directory = Directory.CreateTempSubdirectory("wds-scope-unit").FullName;
        workspace = new WorkspaceStore(Path.Combine(directory, "wds.db"));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WDS_CONN_PG_SCHEMAS"] = schemas })
            .Build();

        return new SchemaScope(config, workspace);
    }

    private static SchemaNode Node(SchemaNodeKind kind, string label) =>
        new(new SchemaNodeRef(kind, [label]), label, true);

    [Fact]
    public void Only_schemas_and_databases_are_filtered()
    {
        var scope = Scope("sales", out _);

        var nodes = scope.Filter(new ConnectionSpecName("id", "PG"),
        [
            Node(SchemaNodeKind.Schema, "public"),
            Node(SchemaNodeKind.Schema, "sales"),
            // A key space, a bucket, a folder: a scope is about schemas, and filtering these would
            // quietly empty the tree on every other kind of engine.
            Node(SchemaNodeKind.Container, "bucket"),
            Node(SchemaNodeKind.RoleFolder, "Roles"),
        ]);

        Assert.Equal(["sales", "bucket", "Roles"], nodes.Select(node => node.Label));
    }

    [Fact]
    public void A_scope_is_matched_without_case()
    {
        var scope = Scope("SALES", out _);

        Assert.Single(scope.Filter(new ConnectionSpecName("id", "PG"),
            [Node(SchemaNodeKind.Schema, "sales"), Node(SchemaNodeKind.Schema, "public")]));
    }

    [Fact]
    public void The_environment_wins_over_what_a_studio_chose()
    {
        var scope = Scope("public", out _);
        scope.Choose("id", ["sales"]);

        Assert.Equal(["public"], scope.InForce(new ConnectionSpecName("id", "PG")));
    }

    [Fact]
    public void System_objects_are_off_until_a_connection_is_told_otherwise()
    {
        var scope = Scope(null, out _);

        Assert.False(scope.SystemObjects("id"));

        scope.ShowSystemObjects("id", true);
        Assert.True(scope.SystemObjects("id"));

        scope.ShowSystemObjects("id", false);
        Assert.False(scope.SystemObjects("id"));
    }
}
