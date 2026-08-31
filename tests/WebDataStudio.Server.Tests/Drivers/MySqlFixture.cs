using MySqlConnector;
using Testcontainers.MySql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public sealed class MySqlFixture : IDriverFixture
{
    private readonly MySqlContainer _container = new MySqlBuilder()
        .WithImage("mysql:8.4").WithDatabase("shop").Build();

    public IDbDriver Driver { get; } = new MySqlDriver();
    public ConnectionSpec Spec => new("t", "test", "mysql", _container.GetConnectionString(),
        false, null, null, ConnectionSource.Stored);
    public string? Schema => "shop";
    public string? SystemSchema => "performance_schema";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = new MySqlConnection(Spec.ConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, active TINYINT(1) NOT NULL);
            INSERT INTO people (name, active) VALUES ('ada',1),('linus',1),('grace',0);
            CREATE TABLE orders (id INT AUTO_INCREMENT PRIMARY KEY,
                                 person_id INT NOT NULL,
                                 total DECIMAL(10,2),
                                 CONSTRAINT fk_orders_person FOREIGN KEY (person_id) REFERENCES people(id));
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class MySqlContractTests(MySqlFixture fixture) : DriverContractTests<MySqlFixture>(fixture);
