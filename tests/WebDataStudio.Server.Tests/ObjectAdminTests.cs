using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebDataStudio.Server.Tests;

/// The rest of the catalogue: what the tree lists beyond tables, row-level security, partitions,
/// refreshing a materialised view, and granting across a whole schema. Every write is a statement
/// the studio hands over — the preview is what runs it.
public class ObjectAdminTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-objadmin").FullName;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE ROLE reporting;
            CREATE TYPE mood AS ENUM ('good', 'bad');
            CREATE DOMAIN positive AS integer CHECK (VALUE > 0);

            CREATE TABLE tenants (id serial PRIMARY KEY, name text NOT NULL, tenant_id integer);
            ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
            CREATE POLICY own_rows ON tenants FOR SELECT TO reporting USING (tenant_id = 1);

            CREATE TABLE events (id serial, at date NOT NULL, note text) PARTITION BY RANGE (at);
            CREATE TABLE events_2026_01 PARTITION OF events
                FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');
            CREATE TABLE events_2026_02 PARTITION OF events
                FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');

            CREATE TABLE numbers (n integer);
            INSERT INTO numbers SELECT generate_series(1, 100);
            CREATE MATERIALIZED VIEW number_count AS SELECT count(*) AS total FROM numbers;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SHOP"] = _container.GetConnectionString(),
                ["WDS_CONN_SHOP_ENGINE"] = "postgresql",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private static async Task<List<(string Ref, string Label)>> NodesAsync(
        HttpClient client, string id, string? parent = null)
    {
        var url = parent is null
            ? $"/api/schema/{id}"
            : $"/api/schema/{id}?parent={Uri.EscapeDataString(parent)}";

        var body = await client.GetFromJsonAsync<JsonElement>(url, TestContext.Current.CancellationToken);

        return [.. body.EnumerateArray().Select(node =>
            (node.GetProperty("ref").GetString()!, node.GetProperty("label").GetString()!))];
    }

    /// Applies a statement the studio built, through the preview it is meant to go through.
    private static async Task ApplyAsync(HttpClient client, string id, string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        var preview = await client.PostAsJsonAsync($"/api/ddl/{id}/script/preview", new { sql }, ct);
        preview.EnsureSuccessStatusCode();

        var hash = (await preview.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("hash").GetString();
        var applied = await client.PostAsJsonAsync($"/api/ddl/{id}/apply", new { hash }, ct);

        applied.EnsureSuccessStatusCode();
    }

    // --- the rest of the catalogue -----------------------------------------------------------

    [Fact]
    public async Task The_tree_lists_extensions_roles_and_the_rest()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var root = await NodesAsync(client, id);
        var labels = root.Select(node => node.Label).ToList();

        Assert.Contains("public", labels);
        Assert.Contains("Extensions", labels);
        Assert.Contains("Roles", labels);
        Assert.Contains("Tablespaces", labels);
        Assert.Contains("Publications", labels);

        var extensions = await NodesAsync(client, id,
            root.First(node => node.Label == "Extensions").Ref);
        Assert.Contains(extensions, node => node.Label.StartsWith("pg_trgm"));

        var roles = await NodesAsync(client, id, root.First(node => node.Label == "Roles").Ref);
        Assert.Contains(roles, node => node.Label.StartsWith("reporting"));
        // A role that cannot log in is a group, and saying so saves a click.
        Assert.Contains(roles, node => node.Label.Contains("(group)") || node.Label.Contains("(superuser)"));
    }

    [Fact]
    public async Task A_schema_lists_its_types_and_domains()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var folders = await NodesAsync(client, id, "Schema:public");
        var types = await NodesAsync(client, id,
            folders.First(node => node.Label == "Types and domains").Ref);

        Assert.Contains(types, node => node.Label == "mood");
        Assert.Contains(types, node => node.Label == "positive");
    }

    [Fact]
    public async Task A_materialised_view_is_in_the_tree_with_its_own_kind()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var folders = await NodesAsync(client, id, "Schema:public");
        var views = await NodesAsync(client, id, folders.First(node => node.Label == "Views").Ref);

        // pg_views leaves materialised views out, so listing only that folder showed none at all —
        // and the refresh action had nothing to appear on.
        var matview = Assert.Single(views, node => node.Label == "number_count");
        Assert.StartsWith("MaterializedView:", matview.Ref);
    }

    // --- row-level security -------------------------------------------------------------------

    [Fact]
    public async Task Policies_are_listed_with_what_they_say()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/policies?ref=Table:public/tenants", ct);

        Assert.True(body.GetProperty("supported").GetBoolean());
        Assert.True(body.GetProperty("enabled").GetBoolean());

        var policy = Assert.Single(body.GetProperty("policies").EnumerateArray());
        Assert.Equal("own_rows", policy.GetProperty("name").GetString());
        Assert.Equal("SELECT", policy.GetProperty("command").GetString());
        Assert.Contains("reporting", policy.GetProperty("roles").GetString());
        Assert.Contains("tenant_id", policy.GetProperty("using").GetString());
    }

    [Fact]
    public async Task A_policy_is_created_and_dropped_through_the_preview()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var built = await client.PostAsJsonAsync(
            $"/api/schema/{id}/policies/statement?ref=Table:public/tenants",
            new
            {
                name = "insert_own", command = "INSERT", roles = "reporting",
                check = "tenant_id = 1",
            }, ct);

        built.EnsureSuccessStatusCode();
        var sql = (await built.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sql").GetString()!;

        Assert.Contains("CREATE POLICY \"insert_own\" ON \"public\".\"tenants\"", sql);
        Assert.Contains("FOR INSERT", sql);
        Assert.Contains("WITH CHECK (tenant_id = 1)", sql);

        await ApplyAsync(client, id, sql);

        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/policies?ref=Table:public/tenants", ct);
        Assert.Equal(2, after.GetProperty("policies").GetArrayLength());

        var dropped = await client.PostAsJsonAsync(
            $"/api/schema/{id}/policies/statement?ref=Table:public/tenants",
            new { name = "insert_own", drop = true }, ct);

        var dropSql = (await dropped.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("sql").GetString()!;
        Assert.Equal("DROP POLICY \"insert_own\" ON \"public\".\"tenants\";", dropSql);

        await ApplyAsync(client, id, dropSql);

        var back = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/policies?ref=Table:public/tenants", ct);
        Assert.Equal(1, back.GetProperty("policies").GetArrayLength());
    }

    /// Turning security on with no policy means "nobody sees anything", so the script says both
    /// halves rather than leaving the second one to be discovered.
    [Fact]
    public async Task Enabling_security_says_both_halves()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var built = await client.PostAsJsonAsync(
            $"/api/schema/{id}/policies/security-statement?ref=Table:public/numbers",
            new { enable = true, force = true }, ct);

        var sql = (await built.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sql").GetString()!;

        Assert.Contains("ENABLE ROW LEVEL SECURITY", sql);
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql);
    }

    [Fact]
    public async Task A_nonsense_policy_command_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/schema/{id}/policies/statement?ref=Table:public/tenants",
            new { name = "x", command = "DESTROY" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- partitions ---------------------------------------------------------------------------

    [Fact]
    public async Task Partitions_are_listed_with_their_bounds_and_sizes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/partitions?ref=Table:public/events", ct);

        Assert.True(body.GetProperty("partitioned").GetBoolean());
        Assert.Equal("RANGE", body.GetProperty("strategy").GetString());
        Assert.Contains("at", body.GetProperty("key").GetString());

        var partitions = body.GetProperty("partitions").EnumerateArray().ToList();
        Assert.Equal(2, partitions.Count);
        Assert.Contains("2026-01-01", partitions[0].GetProperty("bound").GetString());
        Assert.NotNull(partitions[0].GetProperty("sizeBytes").GetInt64().ToString());
    }

    [Fact]
    public async Task A_table_that_is_not_partitioned_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/partitions?ref=Table:public/numbers", ct);

        Assert.True(body.GetProperty("supported").GetBoolean());
        Assert.False(body.GetProperty("partitioned").GetBoolean());
    }

    [Fact]
    public async Task A_partition_is_detached_and_attached_again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var detach = await client.PostAsJsonAsync(
            $"/api/schema/{id}/partitions/statement?ref=Table:public/events",
            new { partition = "events_2026_01", detach = true }, ct);

        var detachSql = (await detach.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("sql").GetString()!;
        Assert.Contains("DETACH PARTITION \"public\".\"events_2026_01\"", detachSql);

        await ApplyAsync(client, id, detachSql);

        var fewer = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/partitions?ref=Table:public/events", ct);
        Assert.Equal(1, fewer.GetProperty("partitions").GetArrayLength());

        var attach = await client.PostAsJsonAsync(
            $"/api/schema/{id}/partitions/statement?ref=Table:public/events",
            new
            {
                partition = "events_2026_01",
                bound = "FOR VALUES FROM ('2026-01-01') TO ('2026-02-01')",
            }, ct);

        await ApplyAsync(client, id,
            (await attach.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sql").GetString()!);

        var back = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/partitions?ref=Table:public/events", ct);
        Assert.Equal(2, back.GetProperty("partitions").GetArrayLength());
    }

    [Fact]
    public async Task Attaching_without_a_bound_says_what_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/schema/{id}/partitions/statement?ref=Table:public/events",
            new { partition = "events_2026_03" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("FOR VALUES", await response.Content.ReadAsStringAsync(ct));
    }

    // --- materialised views --------------------------------------------------------------------

    [Fact]
    public async Task A_materialised_view_is_refreshed_through_the_preview()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var built = await client.PostAsJsonAsync(
            $"/api/schema/{id}/refresh-statement?ref=MaterializedView:public/number_count",
            new { concurrently = false }, ct);

        built.EnsureSuccessStatusCode();
        var sql = (await built.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sql").GetString()!;

        Assert.Equal("REFRESH MATERIALIZED VIEW \"public\".\"number_count\";", sql);
        await ApplyAsync(client, id, sql);
    }

    [Fact]
    public async Task Refreshing_a_table_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/schema/{id}/refresh-statement?ref=Table:public/numbers", new { }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- granting across a schema --------------------------------------------------------------

    [Fact]
    public async Task A_schema_wide_grant_is_one_script()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var built = await client.PostAsJsonAsync($"/api/schema/{id}/privileges/bulk-statement", new
        {
            schema = "public", grantee = "reporting", privileges = new[] { "SELECT" },
            includeFuture = true,
        }, ct);

        built.EnsureSuccessStatusCode();
        var sql = (await built.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sql").GetString()!;

        Assert.Contains("GRANT SELECT ON ALL TABLES IN SCHEMA \"public\" TO \"reporting\";", sql);
        // Without the default privileges, a table created tomorrow is not covered.
        Assert.Contains("ALTER DEFAULT PRIVILEGES", sql);

        await ApplyAsync(client, id, sql);

        var grants = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{id}/privileges?ref=Table:public/numbers", ct);

        Assert.Contains(grants.GetProperty("grants").EnumerateArray(),
            grant => grant.GetProperty("grantee").GetString() == "reporting");
    }

    [Fact]
    public async Task A_bulk_grant_with_no_usable_privilege_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PostAsJsonAsync($"/api/schema/{id}/privileges/bulk-statement", new
        {
            schema = "public", grantee = "reporting", privileges = new[] { "TELEPORT" },
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
