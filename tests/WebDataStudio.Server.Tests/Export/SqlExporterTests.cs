using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Export;
using static WebDataStudio.Server.Tests.Export.DelimitedExporterTests;

namespace WebDataStudio.Server.Tests.Export;

public class SqlExporterTests
{
    private static ExportOptions For(SqlDialect dialect) =>
        ExportOptions.Default with { TableName = "people", Dialect = dialect };

    [Fact]
    public async Task Quotes_identifiers_with_the_target_dialect()
    {
        Assert.Contains("\"people\"", await ExportAsync(new SqlInsertExporter(), For(new PostgreSqlDialect())));
        Assert.Contains("`people`", await ExportAsync(new SqlInsertExporter(), For(new MySqlDialect())));
        Assert.Contains("[people]", await ExportAsync(new SqlInsertExporter(), For(new SqlServerDialect())));
    }

    [Fact]
    public async Task Escapes_a_quote_by_doubling_it()
    {
        var sql = await ExportAsync(new SqlInsertExporter(), For(new PostgreSqlDialect()));
        Assert.Contains("'say \"hi\"'", sql);
    }

    [Fact]
    public async Task Renders_null_as_the_keyword_not_as_a_string()
    {
        var sql = await ExportAsync(new SqlInsertExporter(), For(new PostgreSqlDialect()));
        Assert.Contains("(2, NULL)", sql);
        Assert.DoesNotContain("'NULL'", sql);
    }

    [Fact]
    public async Task Leaves_numbers_unquoted()
    {
        var sql = await ExportAsync(new SqlInsertExporter(), For(new PostgreSqlDialect()));
        Assert.Contains("(1, 'ada')", sql);
    }

    [Fact]
    public async Task Renders_booleans_per_dialect()
    {
        Assert.Contains("TRUE", await ExportAsync(new SqlInsertExporter(), For(new PostgreSqlDialect()), Bool()));
        Assert.Contains("(1)", await ExportAsync(new SqlInsertExporter(), For(new SqlServerDialect()), Bool()));

        static async IAsyncEnumerable<ResultChunk> Bool()
        {
            yield return new ResultChunk.Columns(0, [new ColumnMeta("active", "bool", false)]);
            yield return new ResultChunk.Rows(0, [[true]]);
            yield return new ResultChunk.End(0, 0, 1, false);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Batches_rows_into_multi_row_statements()
    {
        var sql = await ExportAsync(new SqlInsertExporter(), For(new PostgreSqlDialect()), Many(1200));
        var statements = sql.Split("INSERT INTO", StringSplitOptions.RemoveEmptyEntries).Length;

        // 1200 rows at 500 per statement is three statements.
        Assert.Equal(3, statements);

        static async IAsyncEnumerable<ResultChunk> Many(int count)
        {
            yield return new ResultChunk.Columns(0, [new ColumnMeta("id", "int", false)]);
            yield return new ResultChunk.Rows(0, Enumerable.Range(1, count).Select(i => new object?[] { i }).ToArray());
            yield return new ResultChunk.End(0, 0, count, false);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Schema_exporter_writes_create_then_insert()
    {
        var sql = await ExportAsync(new SqlSchemaExporter(), For(new PostgreSqlDialect()));

        Assert.StartsWith("CREATE TABLE \"people\"", sql);
        Assert.Contains("\"id\" INTEGER", sql);
        Assert.True(sql.IndexOf("CREATE TABLE", StringComparison.Ordinal)
                    < sql.IndexOf("INSERT INTO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Schema_exporter_maps_types_to_the_target_engine()
    {
        var sql = await ExportAsync(new SqlSchemaExporter(), For(new SqlServerDialect()));
        Assert.Contains("NVARCHAR(MAX)", sql);
    }

    private static async Task<string> ExportAsync(IResultExporter exporter, ExportOptions options,
        IAsyncEnumerable<ResultChunk>? chunks = null)
    {
        using var stream = new MemoryStream();
        await exporter.WriteAsync(stream, chunks ?? Sample(), options, TestContext.Current.CancellationToken);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
