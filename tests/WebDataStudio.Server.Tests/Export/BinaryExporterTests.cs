using MiniExcelLibs;
using Parquet;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;
using static WebDataStudio.Server.Tests.Export.DelimitedExporterTests;

namespace WebDataStudio.Server.Tests.Export;

public class BinaryExporterTests
{
    private static async Task<byte[]> BytesAsync(IResultExporter exporter, IAsyncEnumerable<ResultChunk> chunks)
    {
        using var stream = new MemoryStream();
        await exporter.WriteAsync(stream, chunks, ExportOptions.Default with { TableName = "people" },
            TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    [Fact]
    public async Task Excel_writes_a_zip_container()
    {
        var bytes = await BytesAsync(new ExcelExporter(), Sample());
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    [Fact]
    public async Task Excel_roundtrips_the_rows_and_header()
    {
        var bytes = await BytesAsync(new ExcelExporter(), Sample());

        using var stream = new MemoryStream(bytes);
        var rows = stream.Query(useHeaderRow: true).Cast<IDictionary<string, object>>().ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal("ada", rows[0]["name"]);
        Assert.Null(rows[1]["name"]);
    }

    [Fact]
    public async Task Excel_truncates_a_cell_beyond_the_sheet_limit()
    {
        var huge = new string('x', 40_000);

        var bytes = await BytesAsync(new ExcelExporter(), Long(huge));
        using var stream = new MemoryStream(bytes);
        var rows = stream.Query(useHeaderRow: true).Cast<IDictionary<string, object>>().ToList();

        Assert.True(((string)rows[0]["note"]).Length <= 32767);

        static async IAsyncEnumerable<ResultChunk> Long(string value)
        {
            yield return new ResultChunk.Columns(0, [new ColumnMeta("note", "text", true)]);
            yield return new ResultChunk.Rows(0, [[value]]);
            yield return new ResultChunk.End(0, 0, 1, false);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Parquet_writes_a_readable_file()
    {
        var bytes = await BytesAsync(new ParquetExporter(), Sample());

        Assert.Equal("PAR1", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        using var stream = new MemoryStream(bytes);
        await using var reader = await ParquetReader.CreateAsync(stream,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, reader.Schema.DataFields.Length);

        using var group = reader.OpenRowGroupReader(0);
        Assert.Equal(3, group.RowCount);

        var names = new string[group.RowCount];
        await group.ReadAsync(reader.Schema.DataFields[1], names.AsMemory(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ada", names[0]);
    }

    [Fact]
    public async Task Parquet_types_an_all_null_column_as_string()
    {
        var bytes = await BytesAsync(new ParquetExporter(), AllNull());

        using var stream = new MemoryStream(bytes);
        await using var reader = await ParquetReader.CreateAsync(stream,
            cancellationToken: TestContext.Current.CancellationToken);
        // Parquet.Net writes strings as a byte-array logical type and reports them back as either
        // string or ReadOnlyMemory<char>, depending on the field. Both mean "textual".
        var clrType = reader.Schema.DataFields[0].ClrType;
        Assert.True(clrType == typeof(string) || clrType == typeof(ReadOnlyMemory<char>), clrType.Name);

        static async IAsyncEnumerable<ResultChunk> AllNull()
        {
            yield return new ResultChunk.Columns(0, [new ColumnMeta("maybe", "text", true)]);
            yield return new ResultChunk.Rows(0, [[null], [null]]);
            yield return new ResultChunk.End(0, 0, 2, false);
            await Task.CompletedTask;
        }
    }
}
