using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class StatementSplitterTests
{
    private static readonly SqlDialect Postgres = new PostgreSqlDialect();
    private static readonly SqlDialect SqlServer = new SqlServerDialect();

    private static string[] Split(string sql, SqlDialect dialect) =>
        StatementSplitter.Split(sql, dialect).Select(s => s.Text.Trim()).ToArray();

    [Fact]
    public void Splits_on_semicolons() =>
        Assert.Equal(["SELECT 1", "SELECT 2"], Split("SELECT 1; SELECT 2;", Postgres));

    [Fact]
    public void Ignores_a_semicolon_inside_a_string_literal() =>
        Assert.Single(Split("SELECT 'a;b'", Postgres));

    [Fact]
    public void Ignores_a_semicolon_inside_a_line_comment() =>
        Assert.Single(Split("SELECT 1 -- a;b\n", Postgres));

    [Fact]
    public void Ignores_a_semicolon_inside_a_block_comment() =>
        Assert.Single(Split("SELECT /* a;b */ 1", Postgres));

    [Fact]
    public void Ignores_a_semicolon_inside_a_quoted_identifier() =>
        Assert.Single(Split("SELECT \"we;ird\" FROM t", Postgres));

    [Fact]
    public void Keeps_a_dollar_quoted_body_intact()
    {
        var sql = "CREATE FUNCTION f() RETURNS int AS $$ BEGIN SELECT 1; RETURN 2; END $$ LANGUAGE plpgsql;";
        Assert.Single(Split(sql, Postgres));
    }

    [Fact]
    public void Splits_sqlserver_batches_on_go() =>
        Assert.Equal(["SELECT 1", "SELECT 2"], Split("SELECT 1\nGO\nSELECT 2\n", SqlServer));

    [Fact]
    public void Does_not_treat_go_inside_an_identifier_as_a_batch_separator() =>
        Assert.Single(Split("SELECT going FROM t", SqlServer));

    [Fact]
    public void Drops_empty_statements() =>
        Assert.Single(Split("SELECT 1;;;", Postgres));

    [Fact]
    public void Reports_the_offset_and_line_of_each_statement()
    {
        var statements = StatementSplitter.Split("SELECT 1;\nSELECT 2;", Postgres);
        Assert.Equal(0, statements[0].StartOffset);
        Assert.Equal(1, statements[0].StartLine);
        Assert.Equal(2, statements[1].StartLine);
    }
}
