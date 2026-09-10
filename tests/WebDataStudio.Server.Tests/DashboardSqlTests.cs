using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The one place a dashboard's range and its variables become SQL — and therefore the one place
/// where a value could become a statement. Most of what is asserted here is that it does not.
public class DashboardSqlTests
{
    private static readonly SqlDialect Postgres = new WebDataStudio.Server.Drivers.PostgreSql.PostgreSqlDialect();
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static DashboardContext Context(Dictionary<string, string[]>? variables = null) =>
        new("now-24h", "now", variables, WidthPx: 800);

    [Fact]
    public void A_statement_without_a_context_is_left_exactly_as_it_was()
    {
        var sql = "SELECT count(*) FROM orders WHERE placed_at > $1";

        Assert.Equal(sql, DashboardSql.Expand(sql, Postgres, null).Sql);
    }

    [Fact]
    public void The_time_filter_binds_the_range_rather_than_writing_it_in()
    {
        var expanded = DashboardSql.Expand(
            "SELECT * FROM orders WHERE $__timeFilter(placed_at)", Postgres, Context(), Now);

        Assert.Contains("placed_at >= CAST(@wds_dash_from AS timestamp)", expanded.Sql);
        Assert.Contains("placed_at < CAST(@wds_dash_to AS timestamp)", expanded.Sql);

        // The instants are values, not text in the statement.
        Assert.Equal(Now.AddDays(-1).ToString("O"), expanded.Parameters["wds_dash_from"]);
        Assert.Equal(Now.ToString("O"), expanded.Parameters["wds_dash_to"]);
        Assert.DoesNotContain("2026-09-09", expanded.Sql);
    }

    [Fact]
    public void The_filter_works_on_a_quoted_or_qualified_column()
    {
        var expanded = DashboardSql.Expand(
            """SELECT 1 WHERE $__timeFilter( o."placed at" )""", Postgres, Context(), Now);

        Assert.Contains("""o."placed at" >= CAST(@wds_dash_from AS timestamp)""", expanded.Sql);
    }

    /// A widget somebody has not finished still runs and still shows rows.
    [Fact]
    public void A_filter_with_no_column_is_simply_true()
    {
        Assert.Contains("1 = 1",
            DashboardSql.Expand("SELECT 1 WHERE $__timeFilter()", Postgres, Context(), Now).Sql);
    }

    [Fact]
    public void The_instants_are_available_as_numbers_and_as_text()
    {
        var expanded = DashboardSql.Expand(
            "SELECT $__from, $__to, $__fromIso, $__toIso, $__intervalMs", Postgres, Context(), Now);

        Assert.Contains(Now.AddDays(-1).ToUnixTimeMilliseconds().ToString(), expanded.Sql);
        Assert.Contains(Now.ToUnixTimeMilliseconds().ToString(), expanded.Sql);
        Assert.Contains($"'{Now.ToString("O")}'", expanded.Sql);
        Assert.DoesNotContain("$__", expanded.Sql);
    }

    /// A bucket width somebody reads on an axis: a day over four hundred buckets is five minutes,
    /// not three hundred and sixteen seconds.
    [Fact]
    public void The_interval_is_a_width_a_person_reads()
    {
        var expanded = DashboardSql.Expand("SELECT $__interval", Postgres, Context(), Now);

        Assert.Contains("'5 minute'", expanded.Sql);
    }

    [Fact]
    public void A_macro_this_studio_does_not_have_is_left_alone()
    {
        var expanded = DashboardSql.Expand("SELECT $__unixEpochFilter(x)", Postgres, Context(), Now);

        Assert.Contains("$__unixEpochFilter(x)", expanded.Sql);
    }

    [Fact]
    public void A_single_value_binds_and_never_reaches_the_statement()
    {
        var expanded = DashboardSql.Expand("SELECT * FROM sales WHERE region = $region",
            Postgres, Context(new() { ["region"] = ["eu-west'; DROP TABLE sales; --"] }), Now);

        Assert.Contains("region = @wds_dash_var_region", expanded.Sql);
        Assert.DoesNotContain("DROP TABLE", expanded.Sql);
        Assert.Equal("eu-west'; DROP TABLE sales; --", expanded.Parameters["wds_dash_var_region"]);
    }

    [Fact]
    public void The_long_and_the_short_spelling_mean_the_same_thing()
    {
        var variables = new Dictionary<string, string[]> { ["region"] = ["eu"] };

        Assert.Equal(
            DashboardSql.Expand("SELECT $region", Postgres, Context(variables), Now).Sql,
            DashboardSql.Expand("SELECT ${region}", Postgres, Context(variables), Now).Sql);
    }

    /// No driver binds an `IN` list, so this is the one place a value reaches the statement text —
    /// quoted by the dialect, with the quote in a name escaped rather than closing the literal.
    [Fact]
    public void A_list_is_inlined_quoted()
    {
        var expanded = DashboardSql.Expand("SELECT * FROM sales WHERE region IN (${region:csv})",
            Postgres, Context(new() { ["region"] = ["eu", "O'Brien"] }), Now);

        Assert.Contains("IN ('eu', 'O''Brien')", expanded.Sql);
        Assert.Empty(expanded.Parameters);
    }

    [Fact]
    public void A_list_nobody_chose_from_is_a_list_that_matches_nothing()
    {
        var expanded = DashboardSql.Expand("SELECT * FROM sales WHERE region IN (${region:csv})",
            Postgres, Context(new() { ["region"] = [] }), Now);

        Assert.Contains("IN (NULL)", expanded.Sql);
    }

    [Fact]
    public void A_value_with_a_line_break_is_refused_and_the_message_names_the_variable()
    {
        var error = Assert.Throws<DashboardValueException>(() => DashboardSql.Expand(
            "SELECT * FROM sales WHERE region IN (${region:csv})",
            Postgres, Context(new() { ["region"] = ["eu\nUNION ALL SELECT secret FROM keys"] }), Now));

        Assert.Contains("region", error.Message);
    }

    [Fact]
    public void Asking_for_a_value_to_be_written_in_raw_is_refused()
    {
        Assert.Throws<DashboardValueException>(() => DashboardSql.Expand(
            "SELECT ${region:raw}", Postgres, Context(new() { ["region"] = ["public.sales"] }), Now));
    }

    [Fact]
    public void A_variable_nobody_defined_stays_as_it_was()
    {
        // `$1` and `$name` are also what a driver's own placeholders look like, so this cannot
        // start rewriting things it was not told about.
        var sql = "SELECT * FROM sales WHERE region = $region AND id = $1";

        Assert.Equal(sql, DashboardSql.Expand(sql, Postgres, Context(), Now).Sql);
    }

    [Fact]
    public void A_relative_range_reads_the_way_dashboards_write_it()
    {
        Assert.Equal(Now.AddHours(-6), DashboardTime.Instant("now-6h", Now));
        Assert.Equal(Now.AddDays(-7), DashboardTime.Instant("now-7d", Now));
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero), DashboardTime.Instant("now/d", Now));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), DashboardTime.Instant("now-9d/M", Now));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DashboardTime.Instant("2026-01-01T00:00:00Z", Now));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1757505600000), DashboardTime.Instant("1757505600000", Now));
    }

    [Fact]
    public void A_range_dragged_backwards_is_read_forwards()
    {
        var range = DashboardTime.Resolve("now", "now-1h", Now);

        Assert.Equal(Now.AddHours(-1), range.From);
        Assert.Equal(Now, range.To);
    }

    /// SQLite compares text timestamps, so its cast is nothing at all — and the filter still has to
    /// come out as valid SQL.
    [Fact]
    public void An_engine_that_needs_no_cast_gets_none()
    {
        var expanded = DashboardSql.Expand("SELECT 1 WHERE $__timeFilter(at)",
            new WebDataStudio.Server.Drivers.Sqlite.SqliteDialect(), Context(), Now);

        Assert.Contains("at >= $wds_dash_from AND at < $wds_dash_to", expanded.Sql);
    }
}
