using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebDataStudio.Server.Tests.Editing;

/// Generating rows into a table whose columns are not text. A value reaches the engine as a string,
/// and PostgreSQL refuses `date = text` rather than guessing — which is what "column signed_up is of
/// type date but expression is of type text" was.
public class GenerateRowsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-generate").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TYPE mood AS ENUM ('ok', 'bad');

            CREATE TABLE members (
                id         serial PRIMARY KEY,
                name       text NOT NULL,
                signed_up  date NOT NULL,
                seen_at    timestamptz,
                balance    numeric(10,2),
                token      uuid,
                feeling    mood,
                active     boolean NOT NULL DEFAULT true
            );

            CREATE TABLE notes (
                id        serial PRIMARY KEY,
                member_id int NOT NULL REFERENCES members(id),
                written   date NOT NULL
            );
            INSERT INTO members (name, signed_up) VALUES ('ada', '2026-01-01');
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
                ["WDS_CONN_SHOP"] = _container.GetConnectionString(),
            })));

    private static async Task<string> IdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    /// Previews the generated rows and applies them, the way the dialog does.
    private static async Task<JsonElement> GenerateAsync(HttpClient client, string conn,
        string objectRef, int rows, object? strategies = null)
    {
        var preview = await client.PostAsJsonAsync(
            $"/api/data/{conn}/generate/preview?ref={Uri.EscapeDataString(objectRef)}",
            new { rows, seed = 42, strategies }, Ct);

        var body = await preview.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        using var previewed = JsonDocument.Parse(body);
        var hash = previewed.RootElement.GetProperty("hash").GetString();

        var applied = await client.PostAsJsonAsync(
            $"/api/data/{conn}/apply-changes?ref={Uri.EscapeDataString(objectRef)}", new { hash }, Ct);

        return JsonDocument.Parse(await applied.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    [Fact]
    public async Task Rows_land_in_a_table_of_dates_numbers_uuids_and_an_enum()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var result = await GenerateAsync(client, conn, "Table:public/members", 25);

        // The whole point: no "expression is of type text".
        Assert.False(result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null,
            error.ToString());
        Assert.Equal(25, result.GetProperty("applied").GetInt32());

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*), count(signed_up), count(balance), count(token), count(feeling)
              FROM members WHERE name <> 'ada'
            """;

        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        Assert.Equal(25, reader.GetInt64(0));
        // The typed columns really hold values, rather than the insert having skipped them.
        Assert.Equal(25, reader.GetInt64(1));
        Assert.True(reader.GetInt64(2) > 0);
        Assert.True(reader.GetInt64(3) > 0);
    }

    [Fact]
    public async Task A_date_column_can_be_asked_for_explicitly()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        // What the dialog sends when somebody picks "date" for a column themselves.
        var result = await GenerateAsync(client, conn, "Table:public/members", 5,
            new Dictionary<string, string> { ["signed_up"] = "date", ["seen_at"] = "date" });

        Assert.Equal(5, result.GetProperty("applied").GetInt32());
    }

    [Fact]
    public async Task A_generated_foreign_key_points_at_a_row_that_exists()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var result = await GenerateAsync(client, conn, "Table:public/notes", 10);

        Assert.Equal(10, result.GetProperty("applied").GetInt32());
    }

    [Fact]
    public async Task A_row_whose_key_the_database_made_up_is_not_offered_as_undoable()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await IdAsync(client);

        var result = await GenerateAsync(client, conn, "Table:public/members", 3);

        // `id` is a serial: the studio never learns which ids these rows got, and deleting by a
        // guess is worse than saying the step cannot be taken back. So it says so.
        Assert.False(result.GetProperty("undoable").GetBoolean());

        using var undo = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/data/{conn}/undo?ref={Uri.EscapeDataString("Table:public/members")}", Ct));

        Assert.False(undo.RootElement.GetProperty("available").GetBoolean());
    }
}
