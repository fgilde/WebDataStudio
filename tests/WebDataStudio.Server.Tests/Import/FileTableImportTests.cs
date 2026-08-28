using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Drivers.ClickHouse;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.Oracle;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Import;

namespace WebDataStudio.Server.Tests.Import;

/// What a file's column becomes in each engine. Pure: this is the mapping, not the loading.
public class ImportTypeTests
{
    [Theory]
    [InlineData("BIGINT", "BIGINT")]
    [InlineData("INTEGER", "INTEGER")]
    [InlineData("BOOLEAN", "BOOLEAN")]
    [InlineData("DOUBLE", "DOUBLE PRECISION")]
    [InlineData("TIMESTAMP", "TIMESTAMP")]
    [InlineData("DATE", "DATE")]
    [InlineData("VARCHAR", "TEXT")]
    [InlineData("DECIMAL(18,3)", "DECIMAL(18,3)")]
    public void PostgreSql_takes_the_names_it_already_uses(string duckdb, string expected) =>
        Assert.Equal(expected, FileTableImport.TargetType(new PostgreSqlDialect(), duckdb));

    [Fact]
    public void MySql_has_no_boolean_and_says_datetime() =>
        Assert.Equal("TINYINT(1)", FileTableImport.TargetType(new MySqlDialect(), "BOOLEAN"));

    [Fact]
    public void SqlServer_says_bit_and_datetime2()
    {
        Assert.Equal("BIT", FileTableImport.TargetType(new SqlServerDialect(), "BOOLEAN"));
        Assert.Equal("DATETIME2", FileTableImport.TargetType(new SqlServerDialect(), "TIMESTAMP"));
    }

    [Fact]
    public void Sqlite_uses_its_own_five()
    {
        // Writing INTEGER and TEXT is what makes a column behave the way the file did.
        Assert.Equal("INTEGER", FileTableImport.TargetType(new SqliteDialect(), "BIGINT"));
        Assert.Equal("REAL", FileTableImport.TargetType(new SqliteDialect(), "DOUBLE"));
        Assert.Equal("TEXT", FileTableImport.TargetType(new SqliteDialect(), "TIMESTAMP"));
    }

    [Fact]
    public void Oracle_says_number_and_clickhouse_says_nullable()
    {
        Assert.Equal("NUMBER(19)", FileTableImport.TargetType(new OracleDialect(), "BIGINT"));
        Assert.Equal("Nullable(Int64)", FileTableImport.TargetType(new ClickHouseDialect(), "BIGINT"));
    }

    [Fact]
    public void A_type_nobody_recognises_becomes_text() =>
        // A column somebody can cast beats an import that refuses.
        Assert.Equal("TEXT", FileTableImport.TargetType(new PostgreSqlDialect(), "MAP(VARCHAR, INT)"));
}

/// The readers, and what they refuse. A `.zip` is not a table, and saying so beats a stack trace.
public class FileSourceTests
{
    [Theory]
    [InlineData("/tmp/people.csv", "read_csv_auto")]
    [InlineData("/tmp/people.parquet", "read_parquet")]
    [InlineData("/tmp/people.ndjson", "read_json_auto")]
    public void A_readable_file_names_its_reader(string path, string reader) =>
        Assert.StartsWith(reader, new LocalFileSource(path).FromClause());

    [Fact]
    public void And_anything_else_says_which_formats_there_are() =>
        Assert.Contains("CSV, TSV, JSON and Parquet",
            Assert.Throws<NotSupportedException>(() => new LocalFileSource("/tmp/notes.zip").FromClause())
                .Message);

    [Fact]
    public void A_parquet_can_be_counted_without_reading_it_and_a_csv_cannot()
    {
        Assert.True(new LocalFileSource("/tmp/a.parquet").CanCountCheaply);
        Assert.False(new LocalFileSource("/tmp/a.csv").CanCountCheaply);
    }

    [Fact]
    public void An_object_in_a_bucket_carries_what_duckdb_needs_before_it_can_read()
    {
        var source = new StorageFileSource("s3://lake/exports/people.parquet",
            ["LOAD httpfs", "CREATE OR REPLACE SECRET wds_storage (TYPE s3)"]);

        Assert.Equal("read_parquet('s3://lake/exports/people.parquet')", source.FromClause());
        Assert.Equal(2, source.Preamble().Count);
    }
}

/// End to end: a CSV and a Parquet become tables in PostgreSQL, with the plan read first.
public class FileTableImportTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    private readonly string _dir = Directory.CreateTempSubdirectory("wds-new-table").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await File.WriteAllTextAsync(Path.Combine(_dir, "people.csv"),
            "id,name,city,born,score,active\n"
            + "1,Ada Lovelace,london,1815-12-10,9.5,true\n"
            + "2,Grace Hopper,new york,1906-12-09,9.75,true\n"
            + "3,Alan Turing,manchester,1912-06-23,9.25,false\n", Ct);

        // A Parquet as well: its types are declared rather than inferred, and its row count is free.
        await using var duck = new DuckDBConnection("Data Source=:memory:");
        await duck.OpenAsync(Ct);
        await using var command = duck.CreateCommand();
        command.CommandText =
            $"COPY (SELECT * FROM read_csv_auto('{Path.Combine(_dir, "people.csv").Replace('\\', '/')}')) "
            + $"TO '{Path.Combine(_dir, "people.parquet").Replace('\\', '/')}' (FORMAT parquet)";
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

    private async Task<JsonElement> PostAsync(HttpClient client, string url, string file)
    {
        using var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(_dir, file), Ct));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bytes, "file", file);

        var response = await client.PostAsync(url, content, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task A_csv_is_planned_before_anything_is_created()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var plan = await PostAsync(client, $"/api/import/{id}/new-table?table=people_csv", "people.csv");

        var columns = plan.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!,
                c => c.GetProperty("targetType").GetString()!);

        // DuckDB's inference, mapped to what PostgreSQL calls those types.
        Assert.Equal("BIGINT", columns["id"]);
        Assert.Equal("TEXT", columns["name"]);
        Assert.Equal("DATE", columns["born"]);
        Assert.Equal("DOUBLE PRECISION", columns["score"]);
        Assert.Equal("BOOLEAN", columns["active"]);

        Assert.Contains("CREATE TABLE", plan.GetProperty("createSql").GetString());
        Assert.Equal(3, plan.GetProperty("preview").GetArrayLength());

        // Nothing was created: this was a plan.
        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var check = db.CreateCommand();
        check.CommandText = "SELECT to_regclass('public.people_csv') IS NULL";
        Assert.True((bool)(await check.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task And_then_it_becomes_a_table_with_the_rows_in_it()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var outcome = await PostAsync(client,
            $"/api/import/{id}/new-table?table=people_applied&apply=true", "people.csv");

        Assert.Equal(3, outcome.GetProperty("rows").GetInt32());
        Assert.Equal("public.people_applied", outcome.GetProperty("table").GetString());

        await using var db = new NpgsqlConnection(_container.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var check = db.CreateCommand();
        check.CommandText = "SELECT name, born, active FROM public.people_applied ORDER BY id";

        await using var reader = await check.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal("Ada Lovelace", reader.GetString(0));
        // The date arrived as a date rather than as text, which is the point of the inference.
        Assert.Equal(new DateOnly(1815, 12, 10), DateOnly.FromDateTime(reader.GetDateTime(1)));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task A_parquet_knows_its_own_row_count_before_it_is_read()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        var plan = await PostAsync(client, $"/api/import/{id}/new-table?table=people_parquet",
            "people.parquet");

        Assert.Equal(3, plan.GetProperty("rows").GetInt64());
    }

    [Fact]
    public async Task A_table_name_that_tries_to_carry_sql_is_refused()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("a\n1\n")), "file", "a.csv");

        var response = await client.PostAsync(
            $"/api/import/{id}/new-table?table=x%3B%20DROP%20TABLE%20people", content, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_format_nothing_reads_says_which_formats_there_are()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var id = await IdAsync(client);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([0x50, 0x4b, 0x03, 0x04]), "file", "notes.zip");

        var response = await client.PostAsync($"/api/import/{id}/new-table?table=notes", content, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CSV, TSV, JSON and Parquet", await response.Content.ReadAsStringAsync(Ct));
    }
}
