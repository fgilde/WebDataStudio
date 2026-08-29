using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Filling an empty development database from another connection: what gets copied, and what is
/// deliberately left alone.
public class SeedFromConnectionTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-seedfrom").FullName;
    private string _source = "";
    private string _target = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _source = Path.Combine(_dir, "source.db");
        _target = Path.Combine(_dir, "target.db");

        await Seed(_source, """
            CREATE TABLE countries (code TEXT PRIMARY KEY, title TEXT);
            INSERT INTO countries VALUES ('de','Germany'),('pt','Portugal');

            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT);
            INSERT INTO customers VALUES (1,'ada'),(2,'grace'),(3,'linus');
            """);

        // The target already has one of them, with something in it somebody is working on.
        await Seed(_target, """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT);
            INSERT INTO customers VALUES (99,'do not overwrite me');
            """);

        static async Task Seed(string path, string sql)
        {
            await using var db = new SqliteConnection($"Data Source={path}");
            await db.OpenAsync(Ct);
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(string json, bool targetReadOnly = false,
        string? targetColour = null)
    {
        var file = Path.Combine(_dir, "seed-from.json");
        File.WriteAllText(file, json);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SRC"] = $"sqlite:///{_source.Replace('\\', '/')}",
                ["WDS_CONN_DST"] = $"sqlite:///{_target.Replace('\\', '/')}",
                ["WDS_CONN_DST_READONLY"] = targetReadOnly ? "true" : "false",
                ["WDS_CONN_DST_COLOR"] = targetColour,
                ["WDS_SEED_FROM_FILE"] = file,
            })));
    }

    private static async Task<List<string>> RowsAsync(string db, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetValue(0).ToString() ?? "");
        return rows;
    }

    private const string Both = """
        [{ "from": "SRC", "to": "DST", "tables": ["countries", "customers"] }]
        """;

    [Fact]
    public async Task Copies_a_table_the_target_does_not_have()
    {
        using var factory = Factory(Both);
        using var client = factory.CreateClient();

        var copied = await factory.Services.GetRequiredService<SeedFromConnection>().RunAsync(Ct);

        Assert.Equal(1, copied);
        Assert.Equal(["Germany", "Portugal"],
            await RowsAsync(_target, "SELECT title FROM countries ORDER BY code"));
    }

    [Fact]
    public async Task Leaves_a_table_that_already_exists_alone()
    {
        using var factory = Factory(Both);
        using var client = factory.CreateClient();

        await factory.Services.GetRequiredService<SeedFromConnection>().RunAsync(Ct);

        // A restart is not a reason to overwrite what somebody has been working on for an hour.
        Assert.Equal(["do not overwrite me"], await RowsAsync(_target, "SELECT name FROM customers"));
    }

    [Fact]
    public async Task Will_not_fill_a_read_only_connection()
    {
        using var factory = Factory(Both, targetReadOnly: true);
        using var client = factory.CreateClient();

        Assert.Equal(0, await factory.Services.GetRequiredService<SeedFromConnection>().RunAsync(Ct));
    }

    [Fact]
    public async Task Will_not_fill_one_marked_as_production()
    {
        // Red is the studio's convention for production, and the seed script honours it too.
        using var factory = Factory(Both, targetColour: "red");
        using var client = factory.CreateClient();

        Assert.Equal(0, await factory.Services.GetRequiredService<SeedFromConnection>().RunAsync(Ct));
    }

    [Fact]
    public async Task One_table_that_will_not_copy_does_not_stop_the_others()
    {
        using var factory = Factory("""
            [{ "from": "SRC", "to": "DST", "tables": ["nonsense", "countries"] }]
            """);
        using var client = factory.CreateClient();

        Assert.Equal(1, await factory.Services.GetRequiredService<SeedFromConnection>().RunAsync(Ct));
    }

    [Fact]
    public async Task Without_a_file_nothing_is_copied_anywhere()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SRC"] = $"sqlite:///{_source.Replace('\\', '/')}",
            })));

        using var client = factory.CreateClient();
        var seeds = factory.Services.GetRequiredService<SeedFromConnection>();

        Assert.False(seeds.Configured);
        Assert.Equal(0, await seeds.RunAsync(Ct));
    }
}
