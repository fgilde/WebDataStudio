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
    public string? SystemSchema => "sys";

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

public class SqlServerContractTests(SqlServerFixture fixture) : DriverContractTests<SqlServerFixture>(fixture)
{
    /// What Rider shows and the tree does not: sys, INFORMATION_SCHEMA, guest, and a schema for
    /// each of the ten fixed database roles. The role schemas exist in every SQL Server database
    /// and are empty in nearly all of them, so hiding them is the whole point — and showing them
    /// when somebody asks is the other half of it.
    [Fact]
    public async Task System_and_fixed_role_schemas_are_hidden_until_asked_for()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec,
            TestContext.Current.CancellationToken);

        async Task<List<string>> SchemasAsync(bool systemObjects) =>
            (await fixture.Driver.IntrospectAsync(session, null,
                TestContext.Current.CancellationToken, systemObjects))
            .Select(node => node.Label).ToList();

        string[] system =
        [
            "sys", "INFORMATION_SCHEMA", "guest",
            "db_owner", "db_accessadmin", "db_securityadmin", "db_ddladmin", "db_backupoperator",
            "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter",
        ];

        var hidden = await SchemasAsync(false);
        Assert.Equal(["dbo"], hidden);

        var shown = await SchemasAsync(true);
        foreach (var schema in system) Assert.Contains(schema, shown);
        Assert.Contains("dbo", shown);
    }

    [Fact]
    public async Task A_script_runs_as_a_batch_so_its_variables_reach_every_statement()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, TestContext.Current.CancellationToken);
        var request = new ScriptRequest("DECLARE @x int = 41;\nSELECT @x + 1 AS answer;", 100, 30, "dbo");

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in fixture.Driver.ExecuteAsync(session, request, TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        Assert.DoesNotContain(chunks, c => c is ResultChunk.Error);
        var rows = Assert.Single(chunks.OfType<ResultChunk.Rows>());
        Assert.Equal(42, Convert.ToInt32(rows.Items[0][0]));
    }

    [Fact]
    public async Task A_read_only_connection_refuses_a_write_later_in_the_batch()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec with { ReadOnly = true },
            TestContext.Current.CancellationToken);
        // One batch now, so the check has to look at every statement in it, not at its first word.
        var request = new ScriptRequest("SELECT 1;\nDELETE FROM people;", 100, 30, "dbo");

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in fixture.Driver.ExecuteAsync(session, request, TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        Assert.Contains(chunks, c => c is ResultChunk.Error { Code: "WDS_READONLY" });
        Assert.DoesNotContain(chunks, c => c is ResultChunk.Rows);
    }
}
