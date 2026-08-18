using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public sealed class SqlServerFixture : IDriverFixture
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public IDbDriver Driver { get; } = new SqlServerDriver();
    public ConnectionSpec Spec => new("t", "test", "sqlserver", _container.GetConnectionString(),
        false, null, null, ConnectionSource.Stored);
    public string? Schema => "dbo";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = new SqlConnection(Spec.ConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INT IDENTITY PRIMARY KEY, name NVARCHAR(100) NOT NULL, active BIT NOT NULL);
            INSERT INTO people (name, active) VALUES ('ada',1),('linus',1),('grace',0);
            CREATE TABLE orders (id INT IDENTITY PRIMARY KEY,
                                 person_id INT NOT NULL CONSTRAINT fk_orders_person REFERENCES people(id),
                                 total DECIMAL(10,2));
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class SqlServerContractTests(SqlServerFixture fixture) : DriverContractTests<SqlServerFixture>(fixture);
