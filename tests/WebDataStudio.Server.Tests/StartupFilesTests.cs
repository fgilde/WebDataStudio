using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Two things a stack can ship with it: the queries everybody on the team needs, and the data that
/// makes a fresh database worth opening.
public class StartupFilesTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-startup").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");

        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private string Queries => Path.Combine(_dir, "queries");
    private string Seeds => Path.Combine(_dir, "seeds");

    private WebApplicationFactory<Program> Factory(
        string? workspace = null, params (string Key, string Value)[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["DB_PATH"] = Path.Combine(_dir, workspace ?? "wds.db"),
                    ["WDS_CONN_SHOP"] = "sqlite:///" + _db.Replace(Path.DirectorySeparatorChar, '/'),
                };

                foreach (var (key, value) in extra) settings[key] = value;
                c.AddInMemoryCollection(settings);
            }));

    private void WriteQuery(string relative, string sql)
    {
        var path = Path.Combine(Queries, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sql);
    }

    private static async Task<long> CountAsync(string db, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    // --- saved queries -----------------------------------------------------------------------

    [Fact]
    public async Task Sql_files_become_saved_queries_with_their_folder_and_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteQuery("reports/top-customers.sql",
            "-- wds:connection SHOP\nSELECT * FROM customers ORDER BY name");
        WriteQuery("stray.sql", "SELECT 1");

        using var factory = Factory(extra: ("WDS_SAVED_QUERIES_DIR", Queries));
        var client = factory.CreateClient();

        Assert.Equal(2, factory.Services.GetRequiredService<SavedQueryImport>().Import());

        var saved = await client.GetFromJsonAsync<JsonElement>("/api/saved-queries", ct);
        var byName = saved.EnumerateArray()
            .ToDictionary(query => query.GetProperty("name").GetString()!, query => query);

        Assert.Equal("reports", byName["top-customers"].GetProperty("folder").GetString());
        Assert.NotNull(byName["top-customers"].GetProperty("connectionId").GetString());
        // A file at the top level has no folder rather than an invented one.
        Assert.Equal(JsonValueKind.Null, byName["stray"].GetProperty("folder").ValueKind);
    }

    /// One setting, two folders: what the repository ships and what an app host wrote. Before this
    /// the second one silently replaced the first.
    [Fact]
    public async Task Two_folders_of_queries_both_arrive()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteQuery("from-the-repo.sql", "SELECT 1");

        var second = Path.Combine(_dir, "queries-inline");
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(second, "from-the-app-host.sql"), "SELECT 2", ct);

        using var factory = Factory(extra: ("WDS_SAVED_QUERIES_DIR", $"{Queries};{second}"));
        var client = factory.CreateClient();

        Assert.Equal(2, factory.Services.GetRequiredService<SavedQueryImport>().Import());

        var saved = await client.GetFromJsonAsync<JsonElement>("/api/saved-queries", ct);
        var names = saved.EnumerateArray()
            .Select(query => query.GetProperty("name").GetString()).ToList();

        Assert.Contains("from-the-repo", names);
        Assert.Contains("from-the-app-host", names);
    }

    /// A restart must not grow the list — the same file is the same query.
    [Fact]
    public void Importing_twice_replaces_rather_than_duplicates()
    {
        WriteQuery("reports/counts.sql", "SELECT count(*) FROM customers");

        using var factory = Factory(extra: ("WDS_SAVED_QUERIES_DIR", Queries));
        var import = factory.Services.GetRequiredService<SavedQueryImport>();
        var workspace = factory.Services.GetRequiredService<WorkspaceStore>();

        import.Import();
        import.Import();

        Assert.Single(workspace.ListSavedQueries());
    }

    [Fact]
    public void A_header_can_override_the_folder()
    {
        WriteQuery("reports/counts.sql", "-- wds:folder Ops\nSELECT 1");

        using var factory = Factory(extra: ("WDS_SAVED_QUERIES_DIR", Queries));
        factory.Services.GetRequiredService<SavedQueryImport>().Import();

        var query = Assert.Single(factory.Services.GetRequiredService<WorkspaceStore>().ListSavedQueries());
        Assert.Equal("Ops", query.Folder);
    }

    [Fact]
    public void Without_a_directory_nothing_is_imported()
    {
        using var factory = Factory();

        Assert.Equal(0, factory.Services.GetRequiredService<SavedQueryImport>().Import());
    }

    [Fact]
    public void A_directory_that_is_not_there_is_a_warning_not_a_crash()
    {
        using var factory = Factory(
            extra: ("WDS_SAVED_QUERIES_DIR", Path.Combine(_dir, "nowhere")));

        Assert.Equal(0, factory.Services.GetRequiredService<SavedQueryImport>().Import());
    }

    // --- seed scripts ------------------------------------------------------------------------

    [Fact]
    public async Task A_seed_script_runs_once_per_content()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Seeds);
        var script = Path.Combine(Seeds, "SHOP.sql");
        await File.WriteAllTextAsync(script, "INSERT INTO customers (name) VALUES ('ada');", ct);

        using var factory = Factory("seed-once.db", ("WDS_SEED_SQL", Seeds));
        var seeds = factory.Services.GetRequiredService<SeedScripts>();

        Assert.Equal(1, await seeds.RunAsync(ct));
        // A restart is not a reason to insert everything again.
        Assert.Equal(0, await seeds.RunAsync(ct));
        Assert.Equal(1, await CountAsync(_db, "customers"));

        // Editing the script is: it is a different seed now.
        await File.WriteAllTextAsync(script, "INSERT INTO customers (name) VALUES ('grace');", ct);
        Assert.Equal(1, await seeds.RunAsync(ct));
        Assert.Equal(2, await CountAsync(_db, "customers"));
    }

    [Fact]
    public async Task One_file_seeds_every_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = Path.Combine(_dir, "seed.sql");
        await File.WriteAllTextAsync(script, "INSERT INTO customers (name) VALUES ('linus');", ct);

        using var factory = Factory("seed-file.db", ("WDS_SEED_SQL", script));

        Assert.Equal(1, await factory.Services.GetRequiredService<SeedScripts>().RunAsync(ct));
        Assert.Equal(1, await CountAsync(_db, "customers"));
    }

    /// Red is the studio's convention for production. Seeding it would be the worst kind of helpful.
    [Fact]
    public async Task A_production_connection_is_not_seeded()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Seeds);
        await File.WriteAllTextAsync(Path.Combine(Seeds, "SHOP.sql"),
            "INSERT INTO customers (name) VALUES ('nope');", ct);

        using var factory = Factory("seed-red.db",
            ("WDS_SEED_SQL", Seeds), ("WDS_CONN_SHOP_COLOR", "red"));

        Assert.Equal(0, await factory.Services.GetRequiredService<SeedScripts>().RunAsync(ct));
        Assert.Equal(0, await CountAsync(_db, "customers"));
    }

    [Fact]
    public async Task A_read_only_connection_is_not_seeded()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Seeds);
        await File.WriteAllTextAsync(Path.Combine(Seeds, "SHOP.sql"),
            "INSERT INTO customers (name) VALUES ('nope');", ct);

        using var factory = Factory("seed-ro.db",
            ("WDS_SEED_SQL", Seeds), ("WDS_CONN_SHOP_READONLY", "true"));

        Assert.Equal(0, await factory.Services.GetRequiredService<SeedScripts>().RunAsync(ct));
        Assert.Equal(0, await CountAsync(_db, "customers"));
    }

    [Fact]
    public async Task A_script_that_fails_is_not_remembered_as_done()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Seeds);
        var script = Path.Combine(Seeds, "SHOP.sql");
        await File.WriteAllTextAsync(script, "INSERT INTO nowhere (x) VALUES (1);", ct);

        using var factory = Factory("seed-bad.db", ("WDS_SEED_SQL", Seeds));
        var seeds = factory.Services.GetRequiredService<SeedScripts>();

        Assert.Equal(0, await seeds.RunAsync(ct));

        // Fixed, and it runs — rather than being skipped because it was "already attempted".
        await File.WriteAllTextAsync(script, "INSERT INTO customers (name) VALUES ('ada');", ct);
        Assert.Equal(1, await seeds.RunAsync(ct));
    }

    [Fact]
    public async Task Without_a_path_nothing_is_seeded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = Factory("seed-none.db");

        Assert.Equal(0, await factory.Services.GetRequiredService<SeedScripts>().RunAsync(ct));
    }
}

/// A database in the same stack is not ready when the studio is, and a seed that runs once and
/// never again is how a stack comes up with an empty schema.
public class SeedRetryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-seed-retry").FullName;

    public void Dispose() => TestDirectory.Remove(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_script_that_could_not_run_is_still_outstanding()
    {
        var seedDir = Path.Combine(_dir, "seed");
        Directory.CreateDirectory(seedDir);
        await File.WriteAllTextAsync(Path.Combine(seedDir, "GONE.sql"), "CREATE TABLE t (id int);", Ct);

        // Nothing is listening there — the same shape as a SQL Server that is still starting.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_GONE"] = "postgres://app:pw@127.0.0.1:1/shop",
                ["WDS_SEED_SQL"] = seedDir,
            })));

        using var client = factory.CreateClient();
        var sweep = await factory.Services.GetRequiredService<SeedScripts>().SweepAsync(Ct);

        Assert.Equal(0, sweep.Seeded);
        Assert.Equal(1, sweep.Pending);
    }

    [Fact]
    public async Task A_connection_that_worked_leaves_nothing_outstanding()
    {
        var db = Path.Combine(_dir, "here.db");
        var seedDir = Path.Combine(_dir, "seed2");
        Directory.CreateDirectory(seedDir);
        await File.WriteAllTextAsync(Path.Combine(seedDir, "HERE.sql"), "CREATE TABLE t (id int);", Ct);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds2.db"),
                ["WDS_CONN_HERE"] = $"sqlite:///{db.Replace('\\', '/')}",
                ["WDS_SEED_SQL"] = seedDir,
            })));

        using var client = factory.CreateClient();
        var seeds = factory.Services.GetRequiredService<SeedScripts>();

        var first = await seeds.SweepAsync(Ct);
        Assert.Equal(1, first.Seeded);
        Assert.Equal(0, first.Pending);

        // And the second sweep does nothing at all: a script that ran is remembered by its hash,
        // which is what makes trying again safe.
        var second = await seeds.SweepAsync(Ct);
        Assert.Equal(0, second.Seeded);
        Assert.Equal(0, second.Pending);
    }
}
