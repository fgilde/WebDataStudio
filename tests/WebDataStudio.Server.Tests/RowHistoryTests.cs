using Microsoft.Data.SqlClient;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;
using WebDataStudio.Server.Tests.Drivers;

namespace WebDataStudio.Server.Tests;

/// "What did this row look like yesterday?" — answered only where the database kept the answer.
public class RowHistoryTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task RunAsync(string sql)
    {
        await using var db = new SqlConnection(fixture.Spec.ConnectionString);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static SchemaNodeRef Ref(string table) => new(SchemaNodeKind.Table, ["dbo", table]);

    [Fact]
    public async Task A_table_the_database_versions_answers_with_what_it_kept()
    {
        await RunAsync("""
            IF OBJECT_ID('dbo.customers') IS NOT NULL
            BEGIN
                ALTER TABLE dbo.customers SET (SYSTEM_VERSIONING = OFF);
                DROP TABLE dbo.customers;
                DROP TABLE IF EXISTS dbo.customers_history;
            END;

            CREATE TABLE dbo.customers (
                id INT PRIMARY KEY,
                name NVARCHAR(100) NOT NULL,
                city NVARCHAR(100) NULL,
                valid_from DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                valid_to DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.customers_history));

            INSERT INTO dbo.customers (id, name, city) VALUES (1, 'ada', 'london');
            """);

        // Two changes, so there is something to look back at.
        await RunAsync("UPDATE dbo.customers SET city = 'oxford' WHERE id = 1;");
        await RunAsync("UPDATE dbo.customers SET name = 'ada l' WHERE id = 1;");

        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var (supported, _) = await RowHistory.SupportsAsync(fixture.Driver, session, Ref("customers"), Ct);
        Assert.True(supported);

        var history = await RowHistory.ReadAsync(fixture.Driver, session, Ref("customers"),
            new Dictionary<string, string?> { ["id"] = "1" }, 50, Ct);

        Assert.True(history.Supported);
        Assert.Equal(3, history.Versions.Count);

        // Every version says when it was the truth, and the current one has no end that matters.
        Assert.All(history.Versions, version => Assert.NotNull(version.From));

        // And what moved between them, which is the reason to read the list at all.
        var changes = history.Versions.SelectMany(version => version.Changed).ToList();
        Assert.Contains("name", changes);
        Assert.Contains("city", changes);
    }

    [Fact]
    public async Task A_plain_table_says_the_database_kept_nothing()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var (supported, note) = await RowHistory.SupportsAsync(fixture.Driver, session, Ref("people"), Ct);

        Assert.False(supported);
        Assert.Contains("not system-versioned", note);
    }

    [Fact]
    public async Task Reading_it_without_a_key_says_why_that_cannot_work()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var history = await RowHistory.ReadAsync(fixture.Driver, session, Ref("customers"),
            new Dictionary<string, string?>(), 50, Ct);

        Assert.Contains("no key columns", history.Note);
        Assert.Empty(history.Versions);
    }
}

/// The engines that keep no history of their own say so rather than inventing one out of the
/// studio's audit trail, which only covers what went through the studio.
public class RowHistoryOnOtherEnginesTests(SqliteFixture fixture) : IClassFixture<SqliteFixture>
{
    [Fact]
    public async Task Sqlite_says_it_keeps_none()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, ct);

        var (supported, note) = await RowHistory.SupportsAsync(fixture.Driver, session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["main", "people"]), ct);

        Assert.False(supported);
        Assert.Contains("keeps no row history", note);
    }
}
