using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public sealed class SqliteFixture : IDriverFixture
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wds-{Guid.NewGuid():N}.db");

    public IDbDriver Driver { get; } = new SqliteDriver();
    public ConnectionSpec Spec => new("t", "test", "sqlite", $"Data Source={_path}",
        false, null, null, ConnectionSource.Stored);
    public string? Schema => null;

    public async ValueTask InitializeAsync()
    {
        await using var db = new SqliteConnection(Spec.ConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, active INTEGER NOT NULL);
            INSERT INTO people (id, name, active) VALUES (1,'ada',1),(2,'linus',1),(3,'grace',0);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, person_id INTEGER NOT NULL REFERENCES people(id), total REAL);
            CREATE INDEX ix_orders_person ON orders(person_id);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
        return ValueTask.CompletedTask;
    }
}

public class SqliteContractTests(SqliteFixture fixture) : DriverContractTests<SqliteFixture>(fixture);
