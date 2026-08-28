using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.PostgreSql;

namespace WebDataStudio.Server.Tests.Analysis;

/// The SQL a rule turns into. Pure: the arguments are parsed rather than pasted, and that is the part
/// worth pinning down.
public class QualityRuleSqlTests
{
    private static readonly PostgreSqlDialect Dialect = new();

    private static QualityRule Rule(QualityKind kind, string? argument = null,
        string column = "city", string schema = "public") =>
        new("r1", "c1", schema, "people", column, kind, argument, null);

    [Fact]
    public void Not_null_counts_the_rows_with_nothing_in_them() =>
        Assert.Equal(
            "SELECT count(*) FROM \"public\".\"people\" WHERE \"city\" IS NULL",
            QualityRules.CountSql(Rule(QualityKind.NotNull), Dialect));

    [Fact]
    public void Unique_counts_the_extra_rows_rather_than_the_groups()
    {
        var sql = QualityRules.CountSql(Rule(QualityKind.Unique), Dialect);

        // Three rows with the same value are two violations, not one group.
        Assert.Contains("SUM(n - 1)", sql);
        Assert.Contains("HAVING count(*) > 1", sql);
    }

    [Fact]
    public void A_range_is_parsed_rather_than_pasted()
    {
        var sql = QualityRules.CountSql(Rule(QualityKind.Range, "0..100", "score"), Dialect);

        Assert.Contains("\"score\" < 0", sql);
        Assert.Contains("\"score\" > 100", sql);
        // NULL is not out of range; that is what NotNull is for.
        Assert.Contains("IS NOT NULL", sql);
    }

    [Fact]
    public void A_range_the_wrong_way_round_still_means_the_same_two_numbers() =>
        Assert.Equal((0m, 100m), QualityRules.ParseRange("100..0"));

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0..")]
    [InlineData("a..b")]
    [InlineData(null)]
    public void And_something_that_is_not_a_range_says_so(string? argument) =>
        Assert.Throws<FormatException>(() =>
            QualityRules.CountSql(Rule(QualityKind.Range, argument), Dialect));

    [Fact]
    public void A_reference_becomes_a_not_exists()
    {
        var sql = QualityRules.CountSql(
            Rule(QualityKind.Referential, "customers.id", "customer_id"), Dialect);

        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("\"customers\"", sql);
        Assert.Contains("r.\"id\" = t.\"customer_id\"", sql);
        // A row with no customer at all is a different rule.
        Assert.Contains("t.\"customer_id\" IS NOT NULL", sql);
    }

    [Fact]
    public void A_reference_can_name_its_schema()
    {
        var sql = QualityRules.CountSql(
            Rule(QualityKind.Referential, "sales.customers.id", "customer_id"), Dialect);

        Assert.Contains("\"sales\".\"customers\"", sql);
    }

    [Fact]
    public void A_reference_that_is_not_a_reference_says_so() =>
        Assert.Throws<FormatException>(() =>
            QualityRules.CountSql(Rule(QualityKind.Referential, "customers"), Dialect));

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("24h", 24 * 60)]
    [InlineData("7d", 7 * 24 * 60)]
    public void An_interval_is_minutes_hours_or_days(string argument, int minutes) =>
        Assert.Equal(minutes, (int)QualityRules.ParseInterval(argument).TotalMinutes);

    [Theory]
    [InlineData("24")]
    [InlineData("24w")]
    [InlineData("-1h")]
    public void And_anything_else_says_so(string argument) =>
        Assert.Throws<FormatException>(() => QualityRules.ParseInterval(argument));

    [Fact]
    public void Freshness_asks_for_the_newest_value_and_compares_it()
    {
        var sql = QualityRules.CountSql(Rule(QualityKind.Freshness, "24h", "updated_at"), Dialect);

        Assert.Contains("max(\"updated_at\")", sql);
        // One row or none: a table is either fresh or it is not.
        Assert.Contains("THEN 1 ELSE 0 END", sql);
    }

    [Fact]
    public void An_expression_is_the_condition_a_bad_row_satisfies()
    {
        var sql = QualityRules.CountSql(
            new QualityRule("r", "c", "public", "orders", "", QualityKind.Expression,
                "total < 0", null), Dialect);

        Assert.Equal("SELECT count(*) FROM \"public\".\"orders\" WHERE total < 0", sql);
    }

    [Fact]
    public void A_table_without_a_schema_is_not_qualified() =>
        Assert.Contains("FROM \"people\"",
            QualityRules.CountSql(Rule(QualityKind.NotNull, schema: ""), Dialect));

    [Fact]
    public void A_result_reads_as_a_sentence()
    {
        var rule = Rule(QualityKind.NotNull) with { Message = "every person needs a city" };
        var failed = new QualityResult(rule, 12, "…", DateTimeOffset.UtcNow, null);
        var passed = new QualityResult(rule, 0, "…", DateTimeOffset.UtcNow, null);
        var broken = new QualityResult(rule, 0, "…", DateTimeOffset.UtcNow, "no such column");

        Assert.Equal("every person needs a city (12 rows)", failed.Describe());
        Assert.Equal("people.city: ok", passed.Describe());
        Assert.Contains("no such column", broken.Describe());
        Assert.False(failed.Passed);
        Assert.True(passed.Passed);
        Assert.False(broken.Passed);
    }

    [Theory]
    [InlineData(5, 5, "unchanged")]
    [InlineData(5, 0, "fixed")]
    [InlineData(0, 3, "new")]
    [InlineData(2, 9, "worse by 7")]
    [InlineData(9, 2, "better by 7")]
    public void A_count_over_time_is_a_direction(long first, long last, string expected) =>
        // A mean over a month says nothing about the direction, which is the only thing anybody asks.
        Assert.Equal(expected, QualityRules.Describe(first, last));

    [Fact]
    public void A_failing_rule_is_a_finding_the_alerts_already_understand()
    {
        var finding = QualityRules.AsFinding(new QualityResult(
            Rule(QualityKind.NotNull), 5, "SELECT count(*) …", DateTimeOffset.UtcNow, null));

        Assert.Equal("data-quality", finding.Category);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("people.city", finding.Title);
    }
}

/// End to end: rules saved, run against real rows, and the answer ordered by what is broken.
public class QualityRunnerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-quality").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id int PRIMARY KEY, name text);
            INSERT INTO customers VALUES (1, 'ada'), (2, 'grace');

            CREATE TABLE orders (
                id int PRIMARY KEY, customer_id int, total numeric, reference text,
                placed timestamptz);
            INSERT INTO orders VALUES
              (1, 1, 10, 'A', now()),
              (2, 99, -5, 'A', now() - interval '10 days'),
              (3, NULL, 20, 'B', now());
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory(string? rulesFile = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, $"wds-{rulesFile is null}.db"),
                ["WDS_CONN_PG"] = _container.GetConnectionString(),
                ["WDS_CONN_PG_ENGINE"] = "postgresql",
                ["WDS_QUALITY_FILE"] = rulesFile,
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private static async Task SaveAsync(HttpClient client, string id, object rule) =>
        (await client.PutAsJsonAsync($"/api/quality/{id}", rule, Ct)).EnsureSuccessStatusCode();

    [Fact]
    public async Task Rules_are_saved_run_and_reported_with_what_is_broken_first()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        await SaveAsync(client, id, new
        {
            id = "", connectionId = id, schema = "public", table = "orders",
            column = "customer_id", kind = "NotNull", argument = (string?)null,
            message = "every order needs a customer", enabled = true,
        });

        await SaveAsync(client, id, new
        {
            id = "", connectionId = id, schema = "public", table = "orders",
            column = "customer_id", kind = "Referential", argument = "customers.id",
            message = (string?)null, enabled = true,
        });

        await SaveAsync(client, id, new
        {
            id = "", connectionId = id, schema = "public", table = "orders",
            column = "total", kind = "Range", argument = "0..1000", message = (string?)null,
            enabled = true,
        });

        await SaveAsync(client, id, new
        {
            id = "", connectionId = id, schema = "public", table = "orders",
            column = "reference", kind = "Unique", argument = (string?)null,
            message = (string?)null, enabled = true,
        });

        await SaveAsync(client, id, new
        {
            id = "", connectionId = id, schema = "public", table = "orders",
            column = "placed", kind = "Freshness", argument = "24h", message = (string?)null,
            enabled = true,
        });

        var response = await client.PostAsync($"/api/quality/{id}/run", null, Ct);
        response.EnsureSuccessStatusCode();

        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(5, report.GetProperty("ran").GetInt32());

        var results = report.GetProperty("results").EnumerateArray()
            .ToDictionary(
                result => result.GetProperty("rule").GetProperty("kind").GetString()!,
                result => result.GetProperty("violations").GetInt64());

        Assert.Equal(1, results["NotNull"]);      // one order with no customer
        Assert.Equal(1, results["Referential"]);  // one pointing at customer 99
        Assert.Equal(1, results["Range"]);        // one with a negative total
        Assert.Equal(1, results["Unique"]);       // reference A twice is one extra row
        // The newest row is now, so the table is fresh.
        Assert.Equal(0, results["Freshness"]);

        Assert.Equal(4, report.GetProperty("failing").GetInt32());
    }

    [Fact]
    public async Task A_rule_that_cannot_be_checked_reports_why_and_the_others_still_run()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        await SaveAsync(client, id, new
        {
            id = "", connectionId = id, schema = "public", table = "orders", column = "nope",
            kind = "NotNull", argument = (string?)null, message = (string?)null, enabled = true,
        });

        await SaveAsync(client, id, new
        {
            id = "", connectionId = id, schema = "public", table = "orders", column = "total",
            kind = "Range", argument = "0..1000", message = (string?)null, enabled = true,
        });

        var report = await (await client.PostAsync($"/api/quality/{id}/run", null, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);

        var errors = report.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("error").GetString())
            .Where(error => error is { Length: > 0 })
            .ToList();

        Assert.Single(errors);
        Assert.Equal(2, report.GetProperty("ran").GetInt32());
    }

    [Fact]
    public async Task A_disabled_rule_is_not_run_and_a_deleted_one_is_gone()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var saved = await (await client.PutAsJsonAsync($"/api/quality/{id}", new
        {
            id = "", connectionId = id, schema = "public", table = "orders", column = "total",
            kind = "Range", argument = "0..1000", message = (string?)null, enabled = false,
        }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(0, (await (await client.PostAsync($"/api/quality/{id}/run", null, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("ran").GetInt32());

        var ruleId = saved.GetProperty("id").GetString();
        (await client.DeleteAsync($"/api/quality/{id}/{ruleId}", Ct)).EnsureSuccessStatusCode();

        var rules = JsonDocument.Parse(await client.GetStringAsync($"/api/quality/{id}", Ct));
        Assert.Empty(rules.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Every_run_is_a_measurement_the_history_keeps()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        await SaveAsync(client, id, new
        {
            id = "", connectionId = id, schema = "public", table = "orders",
            column = "customer_id", kind = "NotNull", argument = (string?)null,
            message = (string?)null, enabled = true,
        });

        // Twice, with a row fixed in between: the direction is the point of keeping the numbers.
        await client.PostAsync($"/api/quality/{id}/run", null, Ct);

        await using (var db = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await db.OpenAsync(Ct);
            await using var fix = db.CreateCommand();
            fix.CommandText = "UPDATE orders SET customer_id = 1 WHERE customer_id IS NULL";
            await fix.ExecuteNonQueryAsync(Ct);
        }

        await client.PostAsync($"/api/quality/{id}/run", null, Ct);

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/quality/{id}/history", Ct);
        var rule = history.GetProperty("rules").EnumerateArray().First();

        Assert.Equal(2, rule.GetProperty("runs").GetInt32());
        Assert.Equal(1, rule.GetProperty("first").GetInt64());
        Assert.Equal(0, rule.GetProperty("last").GetInt64());
        Assert.Equal("fixed", rule.GetProperty("trend").GetString());
        Assert.Equal("orders", rule.GetProperty("table").GetString());
    }

    [Fact]
    public async Task A_rule_the_deployment_ships_runs_and_cannot_be_changed_here()
    {
        var rules = Path.Combine(_dir, "rules.json");

        // The file names the connection the way a person does; the studio resolves it.
        await File.WriteAllTextAsync(rules, """
            [
              {
                "connection": "PG",
                "schema": "public",
                "table": "orders",
                "column": "total",
                "kind": "Range",
                "argument": "0..1000",
                "message": "an order total is never negative"
              },
              {
                "connection": "NOT_HERE",
                "table": "orders",
                "column": "id",
                "kind": "NotNull"
              }
            ]
            """, Ct);

        using var factory = Factory(rules);
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var listed = JsonDocument.Parse(await client.GetStringAsync($"/api/quality/{id}", Ct))
            .RootElement.EnumerateArray().ToList();

        // The rule for a connection this studio does not have is skipped, not fatal.
        var shipped = Assert.Single(listed);
        Assert.True(shipped.GetProperty("fromFile").GetBoolean());
        Assert.Equal("an order total is never negative", shipped.GetProperty("message").GetString());

        // And it runs like any other rule.
        var report = await (await client.PostAsync($"/api/quality/{id}/run", null, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(1, report.GetProperty("ran").GetInt32());
        Assert.Equal(1, report.GetProperty("failing").GetInt32());

        // But the studio does not own it.
        var ruleId = shipped.GetProperty("id").GetString();
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await client.DeleteAsync($"/api/quality/{id}/{ruleId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_rule_without_a_table_is_refused()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var response = await client.PutAsJsonAsync($"/api/quality/{id}", new
        {
            id = "", connectionId = id, schema = "public", table = "  ", column = "total",
            kind = "NotNull", argument = (string?)null, message = (string?)null, enabled = true,
        }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
