using MySqlConnector;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Admin;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Admin;

/// What the server runs on a schedule, read the same way whatever it is called there: a SQL Server
/// Agent job, a pg_cron entry, a MySQL event.
public sealed class SqlServerAgentFixture : IAsyncLifetime
{
    // The Agent's tables live in msdb whether or not the service runs, and these tests read them.
    // Starting the service would only add a race: sp_add_job refuses while the Agent is starting.
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var ct = TestContext.Current.CancellationToken;
        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            EXEC msdb.dbo.sp_add_job @job_name = N'nightly rebuild', @enabled = 1;
            EXEC msdb.dbo.sp_add_jobstep @job_name = N'nightly rebuild', @step_name = N'rebuild',
                 @subsystem = N'TSQL', @command = N'ALTER INDEX ALL ON dbo.people REBUILD';
            EXEC msdb.dbo.sp_add_schedule @schedule_name = N'nightly',
                 @freq_type = 4, @freq_interval = 1, @active_start_time = 20000;
            EXEC msdb.dbo.sp_attach_schedule @job_name = N'nightly rebuild', @schedule_name = N'nightly';
            EXEC msdb.dbo.sp_add_job @job_name = N'paused import', @enabled = 0;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class SqlServerJobTests(SqlServerAgentFixture fixture) : IClassFixture<SqlServerAgentFixture>
{
    private readonly SqlServerDriver _driver = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IDbSession> OpenAsync() =>
        await _driver.OpenAsync(new ConnectionSpec("t", "sql", "sqlserver",
            fixture.ConnectionString, false, null, null, ConnectionSource.Stored), Ct);

    [Fact]
    public async Task The_agent_s_jobs_are_listed_with_their_schedule_and_whether_they_run()
    {
        await using var session = await OpenAsync();

        var jobs = await JobService.ListAsync(_driver, session, Ct);

        var nightly = Assert.Single(jobs, job => job.Name == "nightly rebuild");
        Assert.True(nightly.Enabled);
        Assert.Equal("nightly", nightly.Schedule);
        // The first step's command, so the list says what the job actually does.
        Assert.Contains("REBUILD", nightly.Command);

        var paused = Assert.Single(jobs, job => job.Name == "paused import");
        Assert.False(paused.Enabled);
    }

    [Fact]
    public async Task A_job_that_has_never_run_has_an_empty_history_rather_than_an_error()
    {
        await using var session = await OpenAsync();
        var job = (await JobService.ListAsync(_driver, session, Ct)).First();

        Assert.Empty(await JobService.HistoryAsync(_driver, session, job.Id, 50, Ct));
    }

    [Fact]
    public async Task Enabling_a_job_is_a_statement_that_names_it()
    {
        await using var session = await OpenAsync();
        var job = (await JobService.ListAsync(_driver, session, Ct))
            .Single(entry => entry.Name == "paused import");

        Assert.Equal("EXEC msdb.dbo.sp_update_job @job_name = N'paused import', @enabled = 1",
            JobService.Statement(_driver, "enable", job));
        Assert.Equal("EXEC msdb.dbo.sp_start_job @job_name = N'paused import'",
            JobService.Statement(_driver, "run", job));
    }
}

/// pg_cron is an extension, and most servers do not have it. That is a list with nothing in it, not
/// a panel that fails.
public class PostgreSqlJobTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly PostgreSqlDriver _driver = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _container.StartAsync();
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Without_pg_cron_the_list_is_empty_and_nothing_throws()
    {
        await using var session = await _driver.OpenAsync(new ConnectionSpec("t", "pg", "postgresql",
            _container.GetConnectionString(), false, null, null, ConnectionSource.Stored), Ct);

        Assert.Empty(await JobService.ListAsync(_driver, session, Ct));
        Assert.Empty(await JobService.HistoryAsync(_driver, session, "1", 50, Ct));
    }
}

/// MySQL's event scheduler: the same concept, and the one place where there is no history to read.
public class MySqlJobTests : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder()
        .WithImage("mysql:8.4").WithDatabase("shop").Build();

    private readonly MySqlDriver _driver = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new MySqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE audit (at DATETIME);
            CREATE EVENT nightly_audit ON SCHEDULE EVERY 1 DAY
              DO INSERT INTO audit VALUES (NOW());
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task An_event_reads_as_a_job_with_its_interval_and_its_body()
    {
        await using var session = await _driver.OpenAsync(new ConnectionSpec("t", "my", "mysql",
            _container.GetConnectionString(), false, null, null, ConnectionSource.Stored), Ct);

        var job = Assert.Single(await JobService.ListAsync(_driver, session, Ct));

        Assert.Equal("nightly_audit", job.Name);
        Assert.Equal("every 1 DAY", job.Schedule);
        Assert.Contains("INSERT INTO audit", job.Command);

        Assert.Equal("ALTER EVENT `nightly_audit` DISABLE",
            JobService.Statement(_driver, "disable", job));
        // There is no "run now" here, and saying so beats running the body behind somebody's back.
        Assert.Null(JobService.Statement(_driver, "run", job));
        Assert.DoesNotContain("run", JobService.ActionsFor("mysql").Select(a => a.Id));
    }
}

/// The engines with no scheduler of their own.
public class NoSchedulerTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    [InlineData("mongodb")]
    [InlineData("redis")]
    [InlineData("storage")]
    public void An_engine_without_a_scheduler_says_so(string engine)
    {
        Assert.Null(JobService.SchedulerOf(engine));
        Assert.Empty(JobService.ActionsFor(engine));
    }

    [Fact]
    public void A_pg_cron_id_that_is_not_a_number_is_refused()
    {
        var driver = new PostgreSqlDriver();
        var job = new JobEntry("1; DROP TABLE users", "x", true, "* * * * *", null, null, null, null);

        // The id goes into the statement unquoted, so it is checked rather than trusted.
        Assert.Throws<FormatException>(() => JobService.Statement(driver, "disable", job));
    }
}
