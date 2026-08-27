using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The read of a statement before it runs. Every finding here is something somebody can legitimately
/// mean, so the point is to say what was noticed — never to refuse.
public class SqlInspectionTests
{
    private static readonly PostgreSqlDriver Driver = new();

    private static IReadOnlyList<string> Ids(string sql) =>
        SqlInspections.Inspect(sql, Driver.Dialect).Select(finding => finding.Id).ToList();

    [Fact]
    public void An_update_without_a_where_says_how_many_rows_that_is()
    {
        var finding = Assert.Single(SqlInspections.Inspect("UPDATE people SET city = 'london'", Driver.Dialect));

        Assert.Equal("update-without-where", finding.Id);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("every row in people", finding.Message);
    }

    [Fact]
    public void A_delete_without_a_where_too() =>
        Assert.Contains("delete-without-where", Ids("DELETE FROM orders"));

    [Theory]
    [InlineData("UPDATE people SET city = 'london' WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE placed < now() - interval '1 year'")]
    public void And_neither_when_there_is_one(string sql) => Assert.Empty(Ids(sql));

    [Fact]
    public void A_where_that_is_always_true_filters_nothing() =>
        Assert.Contains("where-always-true", Ids("DELETE FROM orders WHERE 1=1"));

    [Fact]
    public void A_comparison_with_null_is_never_true() =>
        Assert.Contains("equals-null", Ids("SELECT * FROM people WHERE city = NULL"));

    [Fact]
    public void Truncate_and_drop_say_what_they_take_with_them()
    {
        Assert.Contains("truncate", Ids("TRUNCATE TABLE audit"));
        Assert.Contains("drop", Ids("DROP TABLE audit"));
        Assert.Contains("drop", Ids("DROP SCHEMA reporting CASCADE"));
    }

    [Fact]
    public void Two_tables_with_nothing_joining_them_is_a_cross_product() =>
        Assert.Contains("cross-product", Ids("SELECT * FROM people, orders"));

    [Fact]
    public void A_join_with_no_condition_is_the_same_mistake_spelled_differently() =>
        Assert.Contains("join-without-on", Ids("SELECT * FROM people JOIN orders"));

    [Theory]
    [InlineData("SELECT * FROM people p JOIN orders o ON o.person_id = p.id")]
    [InlineData("SELECT * FROM people JOIN orders USING (person_id)")]
    // Said on purpose, which is the whole point of the spelling.
    [InlineData("SELECT * FROM people CROSS JOIN orders")]
    [InlineData("SELECT * FROM people, orders WHERE orders.person_id = people.id")]
    public void And_a_join_that_says_what_it_joins_on_is_not(string sql) => Assert.Empty(Ids(sql));

    [Fact]
    public void A_function_in_the_from_is_one_source_rather_than_three() =>
        // The commas are the function's arguments, not a second and third table.
        Assert.Empty(Ids("SELECT * FROM generate_series(1, 10, 2)"));

    [Fact]
    public void A_comment_is_not_a_statement() =>
        // "-- DELETE FROM orders" has caught out every tool that reads SQL with a substring search.
        Assert.Empty(Ids("-- DELETE FROM orders\nSELECT 1"));

    [Fact]
    public void A_string_literal_is_not_a_clause() =>
        // The WHERE is inside a literal, so this UPDATE really has none.
        Assert.Contains("update-without-where",
            Ids("UPDATE people SET note = 'ask WHERE they live'"));

    [Fact]
    public void Each_statement_in_a_script_is_reported_with_its_own_number_and_line()
    {
        var findings = SqlInspections.Inspect(
            "SELECT 1;\nUPDATE people SET city = 'london';\nDELETE FROM orders;", Driver.Dialect);

        Assert.Equal(2, findings.Count);
        Assert.Equal(2, findings[0].Statement);
        Assert.Equal(2, findings[0].Line);
        Assert.Equal(3, findings[1].Statement);
        Assert.Equal(3, findings[1].Line);
    }

    [Fact]
    public void The_excerpt_is_short_enough_to_read_in_a_dialog()
    {
        var long_ = "UPDATE people SET city = 'london', note = '" + new string('x', 400) + "'";

        Assert.All(SqlInspections.Inspect(long_, Driver.Dialect),
            finding => Assert.True(finding.Excerpt.Length <= 121, finding.Excerpt.Length.ToString()));
    }

    [Fact]
    public void Nothing_is_ever_refused()
    {
        // The findings are advice: there is no severity here that means "will not run".
        var severities = SqlInspections
            .Inspect("TRUNCATE TABLE audit; DELETE FROM orders; DROP TABLE people", Driver.Dialect)
            .Select(finding => finding.Severity)
            .Distinct();

        Assert.All(severities, severity => Assert.Contains(severity, new[] { "warning", "note" }));
    }
}
