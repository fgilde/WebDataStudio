using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// Splitting what pg_get_function_arguments returns. No server needed for this half.
public class FunctionArgumentTests
{
    [Fact]
    public void A_type_with_a_comma_in_it_stays_one_argument()
    {
        var arguments = FunctionInspector.ParseArguments("amount numeric(10, 2), label text");

        Assert.Equal(2, arguments.Count);
        Assert.Equal("numeric(10, 2)", arguments[0].Type);
        Assert.Equal("label", arguments[1].Name);
    }

    [Fact]
    public void Modes_and_defaults_are_read_off_rather_than_left_in_the_type()
    {
        var arguments = FunctionInspector.ParseArguments(
            "IN p_from date, p_to date DEFAULT now(), OUT total numeric");

        Assert.Equal("IN", arguments[0].Mode);
        Assert.False(arguments[0].HasDefault);

        Assert.True(arguments[1].HasDefault);
        Assert.Equal("date", arguments[1].Type);

        // An OUT parameter is a result, not something to type into.
        Assert.Equal("OUT", arguments[2].Mode);
    }

    [Fact]
    public void An_unnamed_argument_is_all_type()
    {
        var arguments = FunctionInspector.ParseArguments("integer, text");

        Assert.Equal("integer", arguments[0].Type);
        Assert.Equal("$1", arguments[0].Name);
        Assert.Equal("text", arguments[1].Type);
    }

    [Fact]
    public void No_arguments_is_no_arguments() => Assert.Empty(FunctionInspector.ParseArguments(""));
}

/// The inspector against a real server: the source, the parameters, and a run that is rolled back
/// with whatever the function raised on the way.
public class FunctionInspectorTests : IAsyncLifetime
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
            CREATE TABLE ledger (id serial PRIMARY KEY, amount numeric NOT NULL);

            CREATE FUNCTION add_up(p_factor integer DEFAULT 1)
            RETURNS numeric LANGUAGE plpgsql AS $$
            DECLARE total numeric;
            BEGIN
              RAISE NOTICE 'counting with factor %', p_factor;
              SELECT coalesce(sum(amount), 0) * p_factor INTO total FROM ledger;
              RETURN total;
            END $$;

            -- Writes, so that a rolled-back trial run can be shown to leave nothing behind.
            CREATE FUNCTION book(p_amount numeric)
            RETURNS integer LANGUAGE plpgsql AS $$
            DECLARE new_id integer;
            BEGIN
              INSERT INTO ledger (amount) VALUES (p_amount) RETURNING id INTO new_id;
              RAISE NOTICE 'booked % as %', p_amount, new_id;
              RETURN new_id;
            END $$;

            CREATE FUNCTION rows_of() RETURNS SETOF ledger LANGUAGE sql AS
              $$ SELECT * FROM ledger ORDER BY id $$;

            INSERT INTO ledger (amount) VALUES (10), (32);
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private SchemaNodeRef Function(string name) =>
        new(SchemaNodeKind.Function, ["public", name]);

    [Fact]
    public async Task The_source_the_language_and_the_parameters_come_back()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);
        var info = await FunctionInspector.ReadAsync(_driver, session, Function("add_up"), Ct);

        Assert.True(info.Supported);
        Assert.Equal("plpgsql", info.Language);
        Assert.Equal("numeric", info.Returns);
        Assert.False(info.ReturnsSet);
        Assert.Contains("RAISE NOTICE", info.Source);

        var argument = Assert.Single(info.Arguments);
        Assert.Equal("p_factor", argument.Name);
        Assert.True(argument.HasDefault);
    }

    [Fact]
    public async Task A_set_returning_function_says_that_it_returns_a_set()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);
        var info = await FunctionInspector.ReadAsync(_driver, session, Function("rows_of"), Ct);

        Assert.True(info.ReturnsSet);
        Assert.Empty(info.Arguments);
    }

    [Fact]
    public async Task A_run_reports_the_result_the_notices_and_the_time()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);
        var run = await FunctionInspector.RunAsync(_driver, session, Function("add_up"), ["3"], Ct);

        Assert.Equal("126", Assert.Single(Assert.Single(run.Rows))?.ToString());
        Assert.Contains("counting with factor 3", Assert.Single(run.Notices));
        Assert.True(run.ElapsedMs >= 0);
        Assert.False(run.Truncated);
    }

    [Fact]
    public async Task What_the_function_wrote_is_rolled_back()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);
        var run = await FunctionInspector.RunAsync(_driver, session, Function("book"), ["99"], Ct);

        Assert.Single(run.Rows);
        Assert.Contains("booked", Assert.Single(run.Notices));

        // The row the function inserted is gone: a trial run that committed would not be a trial.
        await using var check = new NpgsqlConnection(_container.GetConnectionString());
        await check.OpenAsync(Ct);
        await using var command = check.CreateCommand();
        command.CommandText = "SELECT count(*) FROM ledger WHERE amount = 99";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(Ct)));
    }

    [Fact]
    public async Task A_set_returning_function_comes_back_as_rows_with_names()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);
        var run = await FunctionInspector.RunAsync(_driver, session, Function("rows_of"), [], Ct);

        Assert.Equal(["id", "amount"], run.Columns);
        Assert.Equal(2, run.Rows.Count);
    }

    [Fact]
    public async Task Too_many_arguments_is_refused_before_anything_runs()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            FunctionInspector.RunAsync(_driver, session, Function("rows_of"), ["1"], Ct));

        Assert.Contains("takes 0", refused.Message);
    }

    [Fact]
    public async Task A_name_that_is_not_there_is_reported_as_such()
    {
        await using var session = await _driver.OpenAsync(Spec, Ct);
        var info = await FunctionInspector.ReadAsync(_driver, session, Function("nope"), Ct);

        Assert.True(info.Supported);
        Assert.Null(info.Source);
    }
}
