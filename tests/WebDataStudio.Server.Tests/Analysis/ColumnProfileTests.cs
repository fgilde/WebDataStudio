using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;

namespace WebDataStudio.Server.Tests.Analysis;

/// The statement the profile is, and what it suggests. Pure.
public class ColumnProfileSqlTests
{
    private static readonly PostgreSqlDialect Dialect = new();

    private static ColumnMeta Column(string name, string type) => new(name, type, true);

    [Fact]
    public void One_statement_counts_every_column()
    {
        var sql = ColumnProfile.CountSql(Dialect, "\"people\"",
            [Column("id", "integer"), Column("city", "text")]);

        Assert.StartsWith("SELECT count(*) AS wds_rows", sql);
        Assert.Contains("count(\"id\") AS wds_n0", sql);
        Assert.Contains("count(DISTINCT \"id\") AS wds_d0", sql);
        Assert.Contains("min(\"city\") AS wds_lo1", sql);
        Assert.EndsWith("FROM \"people\"", sql);
    }

    [Fact]
    public void A_type_no_engine_groups_by_is_only_counted()
    {
        var sql = ColumnProfile.CountSql(Dialect, "\"files\"", [Column("body", "bytea")]);

        // Asking for count(DISTINCT bytea) fails the whole statement, so it is not asked.
        Assert.Contains("count(\"body\")", sql);
        Assert.DoesNotContain("DISTINCT", sql);
        Assert.DoesNotContain("min(", sql);
    }

    [Fact]
    public void The_sample_reads_text_columns_and_nothing_else()
    {
        var sql = ColumnProfile.SampleSql(Dialect, "\"people\"",
            [Column("id", "integer"), Column("city", "text"), Column("note", "varchar(200)")], 50);

        Assert.Contains("\"city\"", sql);
        Assert.Contains("\"note\"", sql);
        // A number is never an IBAN.
        Assert.DoesNotContain("\"id\"", sql);
        Assert.Contains("LIMIT 50", sql);
    }

    [Fact]
    public void A_table_with_no_text_has_nothing_to_sample() =>
        Assert.Equal("", ColumnProfile.SampleSql(Dialect, "\"t\"", [Column("id", "integer")], 50));

    [Fact]
    public void The_numbers_say_what_they_mean()
    {
        var stat = new ColumnStat("city", "text", 100, 90, 12, "berlin", "zurich");

        Assert.Equal(10, stat.Nulls);
        Assert.Equal(10, stat.NullPercent);
        Assert.False(stat.Unique);
        Assert.False(stat.Constant);

        var key = new ColumnStat("id", "integer", 100, 100, 100, "1", "100");
        Assert.True(key.Unique);

        var forgotten = new ColumnStat("migrated", "boolean", 100, 100, 1, "true", "true");
        Assert.True(forgotten.Constant);
    }

    [Fact]
    public void An_empty_table_suggests_nothing() =>
        Assert.Empty(ColumnProfile.Suggest([new ColumnStat("id", "integer", 0, 0, 0, null, null)]));

    [Fact]
    public void The_suggestions_are_read_off_the_numbers()
    {
        var suggestions = ColumnProfile.Suggest([
            new ColumnStat("id", "integer", 100, 100, 100, "1", "100"),
            new ColumnStat("city", "text", 100, 90, 12, "berlin", "zurich"),
            new ColumnStat("total", "numeric", 100, 100, 80, "0.99", "512.75"),
        ]);

        Assert.Contains(suggestions, s => s.Column == "id" && s.Kind == QualityKind.NotNull);
        Assert.Contains(suggestions, s => s.Column == "id" && s.Kind == QualityKind.Unique);
        // A column that is missing in ten rows today should not be told it never may be.
        Assert.DoesNotContain(suggestions, s => s.Column == "city" && s.Kind == QualityKind.NotNull);
        // A range from the values that are there, in the format the rule parses.
        Assert.Contains(suggestions,
            s => s.Column == "total" && s.Kind == QualityKind.Range && s.Argument == "0.99..512.75");
        Assert.DoesNotContain(suggestions, s => s.Column == "city" && s.Kind == QualityKind.Range);
    }
}

/// The patterns, against the values a real table holds.
public class ColumnProfileTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-profile").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();

        // Names that say nothing: the point is that the values give them away.
        command.CommandText = """
            CREATE TABLE col_dump (
                id      int PRIMARY KEY,
                col_1   text,   -- email addresses
                col_2   text,   -- IBANs
                col_3   text,   -- card numbers, Luhn-valid
                col_4   text,   -- phone numbers
                col_5   text,   -- street addresses
                col_6   text,   -- order numbers: twelve digits, and not a card
                city    text,   -- sometimes missing
                total   numeric,
                flag    boolean,
                body    bytea
            );

            INSERT INTO col_dump
            SELECT n,
                   'person' || n || '@example.com',
                   'DE89370400440532013' || lpad((n % 100)::text, 2, '0'),
                   (ARRAY['4539578763621486', '6011000990139424', '5555555555554444'])[1 + (n % 3)],
                   '+49 30 ' || (1000000 + n),
                   (ARRAY['Bahnhofstrasse 12', 'Rua do Carmo 7', '221 Baker Street'])[1 + (n % 3)],
                   '900000000' || lpad(n::text, 3, '0'),
                   CASE WHEN n % 10 = 0 THEN NULL ELSE (ARRAY['London', 'Lisbon'])[1 + (n % 2)] END,
                   (n % 500) + 0.5,
                   true,
                   decode('cafebabe', 'hex')
              FROM generate_series(1, 60) AS n;
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_PG"] = _container.GetConnectionString(),
                ["WDS_CONN_PG_ENGINE"] = "postgresql",
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> ProfileAsync(HttpClient client, string id) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/data/{id}/profile?ref={Uri.EscapeDataString("Table:public/col_dump")}", Ct);

    [Fact]
    public async Task Every_column_is_counted_in_one_pass()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var profile = await ProfileAsync(client, await IdAsync(client));

        Assert.Equal(60, profile.GetProperty("rows").GetInt32());

        var columns = profile.GetProperty("columns").EnumerateArray()
            .ToDictionary(column => column.GetProperty("name").GetString()!);

        Assert.True(columns["id"].GetProperty("unique").GetBoolean());
        Assert.Equal(0, columns["id"].GetProperty("nulls").GetInt32());

        // Six of sixty rows have no city.
        Assert.Equal(6, columns["city"].GetProperty("nulls").GetInt32());
        Assert.Equal(10, columns["city"].GetProperty("nullPercent").GetDouble());
        Assert.Equal(2, columns["city"].GetProperty("distinct").GetInt32());

        Assert.True(columns["flag"].GetProperty("constant").GetBoolean());

        // A bytea has no distinct count and no min: asking would fail the whole statement.
        Assert.Equal(JsonValueKind.Null, columns["body"].GetProperty("distinct").ValueKind);
        Assert.Equal(JsonValueKind.Null, columns["body"].GetProperty("min").ValueKind);

        Assert.Equal("1.5", columns["total"].GetProperty("min").GetString());
    }

    [Fact]
    public async Task What_a_column_holds_gives_it_away_whatever_it_is_called()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var hints = (await ProfileAsync(client, await IdAsync(client)))
            .GetProperty("hints").EnumerateArray()
            .ToDictionary(hint => hint.GetProperty("column").GetString()!,
                hint => hint.GetProperty("looks").GetString()!);

        Assert.Equal("an email address", hints["col_1"]);
        Assert.Equal("an IBAN", hints["col_2"]);
        Assert.Equal("a card number", hints["col_3"]);
        Assert.Equal("a phone number", hints["col_4"]);
        Assert.Equal("a street address", hints["col_5"]);

        // Twelve digits that fail the check digit are an order number, not a card. Without Luhn
        // this heuristic would mark every reference column in every database.
        Assert.DoesNotContain("col_6", hints.Keys);
        Assert.DoesNotContain("city", hints.Keys);
    }

    [Fact]
    public async Task And_the_numbers_suggest_rules_worth_having()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var suggestions = (await ProfileAsync(client, await IdAsync(client)))
            .GetProperty("suggestions").EnumerateArray()
            .Select(s => (Column: s.GetProperty("column").GetString(),
                          Kind: s.GetProperty("kind").GetString(),
                          Argument: s.GetProperty("argument").GetString()))
            .ToList();

        Assert.Contains(("id", "NotNull", null), suggestions);
        Assert.Contains(("id", "Unique", null), suggestions);
        Assert.Contains(suggestions, s => s.Column == "total" && s.Kind == "Range"
                                          && s.Argument!.Contains(".."));
        // A column with nulls in it today is not suggested as one that never may have them.
        Assert.DoesNotContain(("city", "NotNull", null), suggestions);
    }

    [Fact]
    public async Task A_table_nobody_has_heard_of_says_so()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/data/{await IdAsync(client)}/profile?ref={Uri.EscapeDataString("Table:public/nope")}",
            Ct);

        Assert.True(response.StatusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.BadGateway);
    }
}
