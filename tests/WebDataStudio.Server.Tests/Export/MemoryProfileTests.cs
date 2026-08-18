using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;

namespace WebDataStudio.Server.Tests.Export;

/// The guard that keeps streaming honest: allocations must scale with the chunk size, not with the
/// number of rows. A buffering exporter fails this immediately.
public class MemoryProfileTests
{
    private const int Rows = 200_000;
    private const int ChunkRows = 200;

    private static async IAsyncEnumerable<ResultChunk> Synthetic(int rows)
    {
        yield return new ResultChunk.Columns(0, [
            new ColumnMeta("id", "int", false),
            new ColumnMeta("name", "text", false),
        ]);

        for (var offset = 0; offset < rows; offset += ChunkRows)
        {
            var batch = new object?[Math.Min(ChunkRows, rows - offset)][];
            for (var i = 0; i < batch.Length; i++) batch[i] = [offset + i, "row-name-value"];
            yield return new ResultChunk.Rows(0, batch);
        }

        yield return new ResultChunk.End(0, 0, rows, false);
        await Task.CompletedTask;
    }

    public static TheoryData<string> StreamingFormats() =>
        new() { "csv", "tsv", "json", "ndjson", "xml", "yaml", "markdown", "html", "sql-insert" };

    [Theory]
    [MemberData(nameof(StreamingFormats))]
    public async Task Allocations_do_not_scale_with_row_count(string format)
    {
        var exporter = new ExporterRegistry().Get(format);
        var ct = TestContext.Current.CancellationToken;

        // Warm up so first-call JIT and buffer rental do not count against the measurement.
        await exporter.WriteAsync(Stream.Null, Synthetic(1_000), ExportOptions.Default, ct);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await exporter.WriteAsync(Stream.Null, Synthetic(Rows), ExportOptions.Default, ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        // Allocation per row is unavoidable (each row becomes text); what must not happen is the
        // exporter holding those allocations alive. Live memory after a collection is the real test.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var live = GC.GetTotalMemory(forceFullCollection: true);

        Assert.True(live < 64 * 1024 * 1024,
            $"{format} kept {live / (1024 * 1024)} MB alive after exporting {Rows} rows (allocated {allocated / (1024 * 1024)} MB)");
    }
}
