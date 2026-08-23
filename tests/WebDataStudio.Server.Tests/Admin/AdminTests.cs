using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Admin;
using WebDataStudio.Server.Drivers.Sqlite;

namespace WebDataStudio.Server.Tests.Admin;

public class SystemCommandCatalogTests
{
    [Fact]
    public void Every_engine_in_the_catalogue_describes_its_commands()
    {
        foreach (var engine in new[] { "postgresql", "mysql", "sqlserver", "sqlite" })
            Assert.All(SystemCommandCatalog.For(engine), command =>
            {
                Assert.NotEmpty(command.Label);
                Assert.NotEmpty(command.Description);
            });
    }

    [Fact]
    public void An_unknown_engine_simply_has_no_commands() =>
        Assert.Empty(SystemCommandCatalog.For("redis"));

    [Fact]
    public void A_target_is_quoted_in_the_dialect()
    {
        var command = SystemCommandCatalog.For("postgresql").Single(c => c.Id == "vacuum");
        var sql = SystemCommandCatalog.Render(command, "public.people", new SqliteDialect());

        Assert.Equal("VACUUM (ANALYZE) \"public\".\"people\"", sql);
    }

    [Fact]
    public void A_command_that_needs_a_target_refuses_to_render_without_one()
    {
        var command = SystemCommandCatalog.For("mysql").Single(c => c.Id == "optimize");
        Assert.Throws<InvalidOperationException>(() =>
            SystemCommandCatalog.Render(command, null, new SqliteDialect()));
    }

    [Fact]
    public void A_hostile_target_cannot_break_out_of_the_identifier()
    {
        var command = SystemCommandCatalog.For("mysql").Single(c => c.Id == "optimize");
        var sql = SystemCommandCatalog.Render(command, "people\"; DROP TABLE people; --",
            new SqliteDialect());

        // Quoting doubles the quote instead of ending the identifier.
        Assert.Equal("OPTIMIZE TABLE \"people\"\"; DROP TABLE people; --\"", sql);
    }

    [Fact]
    public void The_destructive_commands_are_flagged_as_such()
    {
        Assert.True(SystemCommandCatalog.For("postgresql").Single(c => c.Id == "vacuum-full").Destructive);
        Assert.False(SystemCommandCatalog.For("postgresql").Single(c => c.Id == "vacuum").Destructive);
    }
}

public class BackupPlanTests
{
    [Fact]
    public void The_postgres_password_travels_in_the_environment_not_in_an_argument()
    {
        var plan = BackupService.Plan(new WebDataStudio.Server.Drivers.PostgreSql.PostgreSqlDriver(),
            new WebDataStudio.Server.Models.ConnectionSpec("p", "P", "postgresql",
                "Host=db;Port=5432;Username=me;Password=s3cret;Database=shop", false, null, null,
                WebDataStudio.Server.Models.ConnectionSource.Environment),
            new BackupOptions(false, false, null));

        Assert.Equal("pg_dump", plan.File);
        Assert.DoesNotContain("s3cret", string.Join(" ", plan.Arguments));
        Assert.Equal("s3cret", plan.Environment["PGPASSWORD"]);
        Assert.Contains("shop", plan.Arguments);
    }

    [Fact]
    public void Schema_only_and_table_filters_reach_the_tool()
    {
        var plan = BackupService.Plan(new WebDataStudio.Server.Drivers.PostgreSql.PostgreSqlDriver(),
            new WebDataStudio.Server.Models.ConnectionSpec("p", "P", "postgresql",
                "Host=db;Username=me;Database=shop", false, null, null,
                WebDataStudio.Server.Models.ConnectionSource.Environment),
            new BackupOptions(true, false, ["people"]));

        Assert.Contains("--schema-only", plan.Arguments);
        Assert.Contains("--table", plan.Arguments);
        Assert.Contains("people", plan.Arguments);
    }

    [Fact]
    public void An_engine_without_a_backup_tool_says_so() =>
        Assert.Throws<NotSupportedException>(() => BackupService.Plan(
            new WebDataStudio.Server.Drivers.DuckDb.DuckDbDriver(),
            new WebDataStudio.Server.Models.ConnectionSpec("d", "D", "duckdb", ":memory:", false, null, null,
                WebDataStudio.Server.Models.ConnectionSource.Environment),
            new BackupOptions(false, false, null)));

    [Fact]
    public void A_tool_that_is_not_installed_is_reported_as_missing() =>
        Assert.False(BackupService.ToolAvailable("wds-no-such-tool"));

    private static WebDataStudio.Server.Models.ConnectionSpec Postgres =>
        new("p", "P", "postgresql", "Host=db;Username=me;Database=shop", false, null, null,
            WebDataStudio.Server.Models.ConnectionSource.Environment);

    private static BackupPlan PgPlan(BackupOptions options) =>
        BackupService.Plan(new WebDataStudio.Server.Drivers.PostgreSql.PostgreSqlDriver(),
            Postgres, options);

    [Fact]
    public void A_custom_dump_is_named_like_one()
    {
        var plan = PgPlan(new BackupOptions(false, false, null, Format: "custom", NoOwner: true));

        Assert.Contains("custom", plan.Arguments);
        Assert.Contains("--no-owner", plan.Arguments);
        // A custom dump called .sql is a file nobody can restore twice.
        Assert.Equal("dump", plan.Extension);
        Assert.Equal("application/octet-stream", plan.ContentType);
    }

    [Fact]
    public void Compression_changes_what_a_plain_dump_is_called()
    {
        var plan = PgPlan(new BackupOptions(false, false, null, Compress: 6));

        Assert.Contains("--compress", plan.Arguments);
        Assert.Contains("6", plan.Arguments);
        Assert.Equal("sql.gz", plan.Extension);
    }

    [Fact]
    public void Clean_belongs_to_a_plain_dump_and_says_so_anywhere_else()
    {
        Assert.Contains("--clean", PgPlan(new BackupOptions(false, false, null, Clean: true)).Arguments);

        var refused = Assert.Throws<NotSupportedException>(() =>
            PgPlan(new BackupOptions(false, false, null, Format: "tar", Clean: true)));
        Assert.Contains("plain", refused.Message);
    }

    [Fact]
    public void A_format_pg_dump_does_not_have_is_refused_rather_than_passed_on() =>
        Assert.Throws<NotSupportedException>(() =>
            PgPlan(new BackupOptions(false, false, null, Format: "parquet")));

    [Fact]
    public void Compression_outside_the_range_is_refused() =>
        Assert.Throws<NotSupportedException>(() =>
            PgPlan(new BackupOptions(false, false, null, Compress: 12)));

    [Fact]
    public void An_option_only_pg_dump_has_is_refused_for_mysqldump()
    {
        var spec = new WebDataStudio.Server.Models.ConnectionSpec("m", "M", "mysql",
            "Server=db;User Id=me;Database=shop", false, null, null,
            WebDataStudio.Server.Models.ConnectionSource.Environment);

        // Ignoring it would produce a file that does not match what the dialog said it asked for.
        Assert.Throws<NotSupportedException>(() => BackupService.Plan(
            new WebDataStudio.Server.Drivers.MySql.MySqlDriver(), spec,
            new BackupOptions(false, false, null, NoOwner: true)));
    }
}

public class AdminEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-admin").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people VALUES (1,'ada'),(2,'linus');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(bool readOnly = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                // The array form is the only one that carries the read-only flag.
                ["WDS_CONNECTIONS"] = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        name = "SHOP", engine = "sqlite",
                        connectionString = $"Data Source={_db}", readOnly,
                    },
                }),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_command_catalogue_is_served_per_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/system-commands/{await IdAsync(client)}", ct);

        Assert.Contains(body.EnumerateArray().Select(e => e.GetProperty("id").GetString()), i => i == "vacuum");
    }

    [Fact]
    public async Task A_catalogued_command_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/admin/system-command/{await IdAsync(client)}",
            new { commandId = "integrity-check" }, ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("PRAGMA integrity_check", body.GetProperty("executed").GetString());
    }

    [Fact]
    public async Task A_command_outside_the_catalogue_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        // The endpoint takes command ids, never SQL — this must not execute.
        var response = await client.PostAsJsonAsync($"/api/admin/system-command/{await IdAsync(client)}",
            new { commandId = "DROP TABLE people" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_read_only_connection_runs_no_maintenance_command()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory(readOnly: true);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/admin/system-command/{await IdAsync(client)}",
            new { commandId = "vacuum" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_engine_without_sessions_says_so_instead_of_failing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/sessions/{await IdAsync(client)}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Contains("session", body.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sqlite_backs_itself_up_and_the_copy_is_a_usable_database()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/admin/backup/{await IdAsync(client)}",
            new { }, ct);

        response.EnsureSuccessStatusCode();
        var copy = Path.Combine(_dir, "copy.db");
        await File.WriteAllBytesAsync(copy, await response.Content.ReadAsByteArrayAsync(ct), ct);

        await using var connection = new SqliteConnection($"Data Source={copy};Mode=ReadOnly");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM people";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync(ct))!);

    }

    [Fact]
    public async Task The_log_endpoint_answers_honestly_when_the_engine_has_no_log()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/logs/{await IdAsync(client)}", ct);

        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.NotNull(body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task User_management_is_refused_where_the_engine_has_none()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/users/{await IdAsync(client)}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_connection_is_a_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/system-commands/nope", ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
