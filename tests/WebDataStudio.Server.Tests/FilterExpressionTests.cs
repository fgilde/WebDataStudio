using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The filter language, checked as behaviour rather than as SQL text: every case in
/// tests/filter-cases.json is built into a condition and run against SQLite with that one value in
/// the table. The browser's evaluator reads the same file, so a case that disagrees means a filter
/// would mean two different things depending on where it ran.
public class FilterCaseTests
{
    private sealed record Case(string Filter, string Kind, JsonElement Value, bool Matches, string? Why);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WebDataStudio.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static JsonDocument Corpus() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "filter-cases.json")));

    public static TheoryData<int> CaseIndexes()
    {
        using var corpus = Corpus();
        var data = new TheoryData<int>();
        for (var index = 0; index < corpus.RootElement.GetProperty("cases").GetArrayLength(); index++)
            data.Add(index);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void A_filter_keeps_exactly_the_rows_the_corpus_says(int index)
    {
        using var corpus = Corpus();
        var now = DateTime.ParseExact(corpus.RootElement.GetProperty("now").GetString()!,
            "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var entry = corpus.RootElement.GetProperty("cases")[index];
        var filter = entry.GetProperty("filter").GetString()!;
        var kindName = entry.GetProperty("kind").GetString()!;
        var value = entry.GetProperty("value");
        var expected = entry.GetProperty("matches").GetBoolean();

        var kind = kindName switch
        {
            "text" => FilterKind.Text,
            "number" => FilterKind.Number,
            "date" => FilterKind.Date,
            "boolean" => FilterKind.Boolean,
            _ => throw new ArgumentException($"unknown kind '{kindName}'"),
        };

        var dialect = new SqliteDriver().Dialect;
        var condition = FilterExpression.Build(dialect, "\"v\"", kind, filter, "f", now);

        Assert.Equal(expected, Survives(kind, value, condition));
    }

    /// One row, one column, one condition. SQLite is the stand-in for every engine here: what it
    /// answers is what the language means, and the browser has to agree.
    private static bool Survives(FilterKind kind, JsonElement value, FilterCondition condition)
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();

        // NUMERIC affinity, so a parameter that arrives as text is compared as the number it is —
        // which is what every server engine does with a string parameter as well.
        var type = kind switch
        {
            FilterKind.Number or FilterKind.Boolean => "NUMERIC",
            _ => "TEXT",
        };

        using (var create = db.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE t (v {type})";
            create.ExecuteNonQuery();
        }

        using (var insert = db.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t (v) VALUES ($v)";
            insert.Parameters.AddWithValue("$v", value.ValueKind switch
            {
                JsonValueKind.Null => DBNull.Value,
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                JsonValueKind.Number => value.GetDecimal(),
                _ => value.GetString()!,
            });
            insert.ExecuteNonQuery();
        }

        using var select = db.CreateCommand();
        select.CommandText = condition.IsEmpty
            ? "SELECT count(*) FROM t"
            : $"SELECT count(*) FROM t WHERE {condition.Sql}";

        foreach (var (name, parameter) in FilterExpression.AsText(condition.Parameters))
            select.Parameters.AddWithValue("$" + name, (object?)parameter ?? DBNull.Value);

        return Convert.ToInt64(select.ExecuteScalar()) == 1;
    }
}

public class FilterExpressionTests
{
    private static readonly SqlDialectHolder Sqlite = new();

    private sealed class SqlDialectHolder
    {
        public WebDataStudio.Server.Drivers.Abstractions.SqlDialect Dialect { get; } =
            new SqliteDriver().Dialect;
    }

    private static FilterCondition Build(string filter, FilterKind kind = FilterKind.Text) =>
        FilterExpression.Build(Sqlite.Dialect, "\"v\"", kind, filter, "f");

    [Fact]
    public void Nothing_typed_is_no_condition_at_all()
    {
        Assert.True(Build("").IsEmpty);
        Assert.True(Build("   ").IsEmpty);
        Assert.True(Build(",,").IsEmpty);
    }

    [Fact]
    public void A_value_never_reaches_the_sql_itself()
    {
        // Quoted, so the whole thing is one term rather than three words joined by AND.
        var condition = Build("^\"Rob'; DROP TABLE t; --\"");

        Assert.DoesNotContain("DROP", condition.Sql);
        Assert.Equal("rob'; drop table t; --%",
            condition.Parameters.Values.Single()?.ToString());
    }

    [Fact]
    public void Every_term_gets_its_own_parameter()
    {
        var condition = Build(">10 <20", FilterKind.Number);

        Assert.Equal(2, condition.Parameters.Count);
        Assert.Contains(" AND ", condition.Sql);
    }

    [Fact]
    public void A_paragraph_pasted_into_the_box_is_cut_off_rather_than_run()
    {
        var condition = Build(string.Join(" ", Enumerable.Range(0, 200).Select(n => $"w{n}")));

        // A filter box is not a query language; 32 terms is already more than anybody types.
        Assert.Equal(32, condition.Parameters.Count);
    }

    [Fact]
    public void The_declared_type_decides_what_a_comparison_means()
    {
        Assert.Equal(FilterKind.Number, FilterExpression.KindOf("integer"));
        Assert.Equal(FilterKind.Number, FilterExpression.KindOf("numeric(10,2)"));
        Assert.Equal(FilterKind.Number, FilterExpression.KindOf("bigserial"));
        Assert.Equal(FilterKind.Date, FilterExpression.KindOf("timestamp with time zone"));
        Assert.Equal(FilterKind.Date, FilterExpression.KindOf("DATE"));
        Assert.Equal(FilterKind.Boolean, FilterExpression.KindOf("boolean"));
        Assert.Equal(FilterKind.Boolean, FilterExpression.KindOf("bit"));
        Assert.Equal(FilterKind.Text, FilterExpression.KindOf("character varying(50)"));
        Assert.Equal(FilterKind.Text, FilterExpression.KindOf("jsonb"));
    }

    [Fact]
    public void A_week_starts_on_monday()
    {
        // Sunday, 2026-08-23. A week that starts on Sunday surprises everybody who is not American.
        var period = FilterExpression.Period("THIS WEEK", new DateTime(2026, 8, 23, 14, 0, 0));

        Assert.NotNull(period);
        Assert.Equal(new DateTime(2026, 8, 17), period!.Value.From);
        Assert.Equal(new DateTime(2026, 8, 24), period.Value.To);
    }

    [Fact]
    public void Dates_and_numbers_are_spelled_the_same_on_every_machine()
    {
        var text = FilterExpression.AsText(new Dictionary<string, object?>
        {
            ["a"] = new DateTime(2026, 8, 23, 14, 30, 0),
            ["b"] = 1.5m,
            ["c"] = true,
            ["d"] = null,
        });

        // Never the current culture: "23.08.2026" is a date on one machine and an error on the next.
        Assert.Equal("2026-08-23 14:30:00", text["a"]);
        Assert.Equal("1.5", text["b"]);
        // Not "true": a bit column takes 1, and every other engine takes it too.
        Assert.Equal("1", text["c"]);
        Assert.Null(text["d"]);
    }
}
