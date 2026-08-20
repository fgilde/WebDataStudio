using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests.Admin;

/// "What is going on right now" against a real server, including a lock somebody is actually
/// waiting for — the question every incident starts with.
public class ActivityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly IDbDriver _driver = new PostgreSqlDriver();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConnectionSpec Spec => new("t", "test", "postgresql", _container.GetConnectionString(),
        false, null, null, ConnectionSource.Stored);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE people (id int PRIMARY KEY, name text);
            INSERT INTO people VALUES (1, 'ada'), (2, 'linus');
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task An_idle_server_reports_empty_lists_rather_than_failing()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);

        var activity = await ServerActivity.ReadAsync(_driver, session, Ct);

        Assert.NotNull(activity.Operations);
        Assert.Empty(activity.Waits);
    }

    [Fact]
    public async Task A_blocked_session_names_the_one_blocking_it()
    {
        // Hold a row lock in one connection, try to update the same row in another, and the server
        // has to tell us who is waiting for whom.
        await using var holder = new NpgsqlConnection(_container.GetConnectionString());
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockCommand = holder.CreateCommand())
        {
            lockCommand.CommandText = "UPDATE people SET name = 'locked' WHERE id = 1";
            await lockCommand.ExecuteNonQueryAsync(Ct);
        }

        await using var blocked = new NpgsqlConnection(_container.GetConnectionString());
        await blocked.OpenAsync(Ct);
        await using var blockedCommand = blocked.CreateCommand();
        blockedCommand.CommandText = "UPDATE people SET name = 'waiting' WHERE id = 1";
        var waiting = blockedCommand.ExecuteNonQueryAsync(Ct);

        try
        {
            // Give the second statement a moment to start waiting.
            await Task.Delay(1_500, Ct);

            await using var session = await _driver.OpenAsync(Spec, Ct);
            var activity = await ServerActivity.ReadAsync(_driver, session, Ct);

            var wait = Assert.Single(activity.Waits);
            Assert.False(string.IsNullOrWhiteSpace(wait.Blocker));
            Assert.False(string.IsNullOrWhiteSpace(wait.Blocked));
            Assert.NotEqual(wait.Blocker, wait.Blocked);
            Assert.Contains("UPDATE", wait.Statement ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
            await waiting;
        }
    }

    [Fact]
    public async Task A_long_running_statement_shows_up_with_its_age()
    {
        await using var slow = new NpgsqlConnection(_container.GetConnectionString());
        await slow.OpenAsync(Ct);
        await using var slowCommand = slow.CreateCommand();
        slowCommand.CommandText = "SELECT pg_sleep(4)";
        var running = slowCommand.ExecuteNonQueryAsync(Ct);

        try
        {
            await Task.Delay(1_800, Ct);

            await using var session = await _driver.OpenAsync(Spec, Ct);
            var activity = await ServerActivity.ReadAsync(_driver, session, Ct);

            var operation = Assert.Single(activity.Operations,
                entry => (entry.Statement ?? "").Contains("pg_sleep"));
            Assert.True(operation.ElapsedMs > 500, $"the statement reports {operation.ElapsedMs} ms");
        }
        finally
        {
            await running;
        }
    }

    [Fact]
    public async Task Replication_on_a_server_with_no_replicas_is_an_empty_list()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);

        Assert.Empty(await ServerActivity.ReplicationAsync(_driver, session, Ct));
    }

    // A driver that does not answer must not be asked: the tab is capability-gated, and the flag has
    // to match reality.
    [Fact]
    public void The_engines_that_answer_declare_it()
    {
        Assert.True(new PostgreSqlDriver().Caps.ActivityProgress);
        Assert.False(new WebDataStudio.Server.Drivers.Sqlite.SqliteDriver().Caps.ActivityProgress);
    }
}
