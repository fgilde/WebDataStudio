using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public sealed class PostgreSqlFixture : IDriverFixture
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    public IDbDriver Driver { get; } = new PostgreSqlDriver();
    public ConnectionSpec Spec => new("t", "test", "postgresql", _container.GetConnectionString(),
        false, null, null, ConnectionSource.Stored);
    public string? Schema => "public";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = new NpgsqlConnection(Spec.ConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id serial PRIMARY KEY, name text NOT NULL, active boolean NOT NULL);
            INSERT INTO people (name, active) VALUES ('ada', true), ('linus', true), ('grace', false);
            CREATE TABLE orders (id serial PRIMARY KEY,
                                 person_id integer NOT NULL REFERENCES people(id),
                                 total numeric(10,2));
            CREATE INDEX ix_orders_person ON orders(person_id);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class PostgreSqlContractTests(PostgreSqlFixture fixture) : DriverContractTests<PostgreSqlFixture>(fixture);
