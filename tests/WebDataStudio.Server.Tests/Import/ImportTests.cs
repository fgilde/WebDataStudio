using System.Text;
using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Import;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Import;

public class ImportSourceTests
{
    private static Stream Text(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Csv_preview_reads_the_header_and_sample_rows()
    {
        var preview = await new CsvImportSource().PreviewAsync(
            Text("id,name\n1,ada\n2,linus\n"), ImportSettings.Default, TestContext.Current.CancellationToken);

        Assert.Equal(["id", "name"], preview.Columns);
        Assert.Equal(2, preview.SampleRows.Count);
        Assert.Equal("ada", preview.SampleRows[0][1]);
    }

    [Fact]
    public async Task Csv_without_a_header_names_columns_by_position()
    {
        var preview = await new CsvImportSource().PreviewAsync(
            Text("1,ada\n"), ImportSettings.Default with { HasHeader = false }, TestContext.Current.CancellationToken);

        Assert.Equal(["column1", "column2"], preview.Columns);
    }

    [Fact]
    public async Task Csv_detects_integer_and_timestamp_columns()
    {
        var preview = await new CsvImportSource().PreviewAsync(
            Text("id,when,label\n1,2026-01-02T03:04:05,x\n2,2026-01-03T00:00:00,y\n"),
            ImportSettings.Default, TestContext.Current.CancellationToken);

        Assert.Equal("integer", preview.DetectedTypes[0]);
        Assert.Equal("timestamp", preview.DetectedTypes[1]);
        Assert.Equal("text", preview.DetectedTypes[2]);
    }

    [Fact]
    public async Task Csv_honours_a_custom_delimiter()
    {
        var preview = await new CsvImportSource().PreviewAsync(
            Text("id;name\n1;ada\n"), ImportSettings.Default with { Delimiter = ";" },
            TestContext.Current.CancellationToken);

        Assert.Equal(["id", "name"], preview.Columns);
    }

    [Fact]
    public async Task Json_preview_reads_keys_from_the_first_object()
    {
        var preview = await new JsonImportSource().PreviewAsync(
            Text("""[{"id":1,"name":"ada"},{"id":2,"name":"linus"}]"""),
            ImportSettings.Default, TestContext.Current.CancellationToken);

        Assert.Equal(["id", "name"], preview.Columns);
        Assert.Equal(2, preview.SampleRows.Count);
    }

    [Fact]
    public async Task Json_fills_a_missing_key_with_null_instead_of_shifting_the_row()
    {
        var source = new JsonImportSource();
        var rows = new List<object?[]>();
        await foreach (var row in source.ReadAsync(
            Text("""[{"id":1,"name":"ada"},{"id":2}]"""), ImportSettings.Default,
            TestContext.Current.CancellationToken))
            rows.Add(row);

        Assert.Equal(2, rows.Count);
        Assert.Null(rows[1][1]);
    }

    [Fact]
    public void Format_is_detected_from_the_file_name()
    {
        Assert.Equal("csv", ImportSources.DetectFormat("people.csv"));
        Assert.Equal("xlsx", ImportSources.DetectFormat("people.XLSX"));
        Assert.Equal("json", ImportSources.DetectFormat("people.json"));
        Assert.Equal("sql", ImportSources.DetectFormat("dump.sql"));
        Assert.Null(ImportSources.DetectFormat("people.docx"));
    }
}

public class ImportExecutionTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-import").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "demo.db");
        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    private ConnectionSpec Spec(bool readOnly = false) =>
        new("t", "demo", "sqlite", $"Data Source={_db}", readOnly, null, null, ConnectionSource.Stored);

    private static async IAsyncEnumerable<object?[]> Rows(params object?[][] rows)
    {
        foreach (var row in rows) yield return row;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Inserts_every_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var driver = new SqliteDriver();
        await using var session = await driver.OpenAsync(Spec(), ct);

        var result = await new ImportService().ExecuteAsync(driver, session, "people",
            ["id", "name"], Rows([1, "ada"], [2, "linus"], [3, "grace"]), ct);

        Assert.Equal(3, result.Inserted);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Reports_a_failing_row_and_keeps_the_others()
    {
        var ct = TestContext.Current.CancellationToken;
        var driver = new SqliteDriver();
        await using var session = await driver.OpenAsync(Spec(), ct);

        // The second row violates NOT NULL on name.
        var result = await new ImportService().ExecuteAsync(driver, session, "people",
            ["id", "name"], Rows([1, "ada"], [2, null], [3, "grace"]), ct);

        Assert.Equal(2, result.Inserted);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Refuses_a_read_only_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var driver = new SqliteDriver();
        await using var session = await driver.OpenAsync(Spec(readOnly: true), ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ImportService().ExecuteAsync(
            driver, session, "people", ["id", "name"], Rows([1, "ada"]), ct));
    }
}
