using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;

namespace WebDataStudio.Server.Tests.Export;

/// The guard that keeps streaming honest: an exporter may write a row and forget it, never collect
/// rows until the end. A buffering exporter fails this immediately.
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
    public async Task An_exporter_holds_on_to_nothing_it_has_written(string format)
    {
        var exporter = new ExporterRegistry().Get(format);
        var ct = TestContext.Current.CancellationToken;

        // Warm up so first-call JIT and buffer rental do not count against the measurement.
        await exporter.WriteAsync(Stream.Null, Synthetic(1_000), ExportOptions.Default, ct);

        // Live memory used to be the measure, but it is process-wide: a sibling test holding a web
        // host alive failed this one. What the test actually means is that the exporter kept no
        // reference to the rows it wrote, and that is a question about those objects rather than
        // about how much memory the process happens to hold.
        var batches = new List<WeakReference>();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        await exporter.WriteAsync(Stream.Null, Tracked(Rows, batches), ExportOptions.Default, ct);

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The last batch can still be held by a local in the export loop; everything before it has
        // to be collectable. A buffering exporter keeps all of them.
        var alive = batches.Count(batch => batch.IsAlive);
        Assert.True(alive <= 1,
            $"{format} still holds {alive} of {batches.Count} row batches after exporting " +
            $"{Rows} rows (allocated {allocated / (1024 * 1024)} MB)");
    }

    /// The same rows as <see cref="Synthetic"/>, with a weak handle kept on every batch.
    private static async IAsyncEnumerable<ResultChunk> Tracked(int rows, List<WeakReference> batches)
    {
        await foreach (var chunk in Synthetic(rows))
        {
            if (chunk is ResultChunk.Rows rows_) batches.Add(new WeakReference(rows_.Items));
            yield return chunk;
        }
    }
}
