using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;

namespace WebDataStudio.Server.Tests.Export;

public class DelimitedExporterTests
{
    internal static async IAsyncEnumerable<ResultChunk> Sample()
    {
        yield return new ResultChunk.Columns(0, [
            new ColumnMeta("id", "int", false),
            new ColumnMeta("name", "text", true),
        ]);
        yield return new ResultChunk.Rows(0, [[1, "ada"], [2, null], [3, "say \"hi\""]]);
        yield return new ResultChunk.End(0, 0, 1, false);
        await Task.CompletedTask;
    }

    internal static async Task<string> ExportAsync(IResultExporter exporter, ExportOptions options)
    {
        using var stream = new MemoryStream();
        await exporter.WriteAsync(stream, Sample(), options, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Writes_a_header_and_rows()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default);
        Assert.StartsWith("id,name", csv);
        Assert.Contains("1,ada", csv);
    }

    [Fact]
    public async Task Renders_null_as_the_configured_text()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default with { NullText = "\\N" });
        Assert.Contains("2,\\N", csv);
    }

    [Fact]
    public async Task Quotes_a_value_containing_a_quote()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default);
        Assert.Contains("\"say \"\"hi\"\"\"", csv);
    }

    [Fact]
    public async Task Quotes_a_value_containing_the_delimiter()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default with { NullText = "a,b" });
        Assert.Contains("\"a,b\"", csv);
    }

    [Fact]
    public async Task Omits_the_header_when_asked()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default with { Header = false });
        Assert.StartsWith("1,ada", csv);
    }

    [Fact]
    public async Task Quotes_every_field_when_asked()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default with { QuoteAll = true });
        Assert.Contains("\"1\",\"ada\"", csv);
    }

    [Fact]
    public async Task Tsv_uses_tabs()
    {
        var tsv = await ExportAsync(new DelimitedExporter("tsv", "\t"), ExportOptions.Default);
        Assert.Contains("1\tada", tsv);
    }
}

public class ExporterRegistryTests
{
    [Fact]
    public void Resolves_every_advertised_format()
    {
        var registry = new ExporterRegistry();
        foreach (var exporter in registry.All())
            Assert.Same(exporter, registry.Get(exporter.Format));
    }

    [Fact]
    public void Unknown_format_throws() =>
        Assert.Throws<NotSupportedException>(() => new ExporterRegistry().Get("wingdings"));

    [Fact]
    public void Every_exporter_declares_a_content_type_and_extension()
    {
        foreach (var exporter in new ExporterRegistry().All())
        {
            Assert.False(string.IsNullOrWhiteSpace(exporter.ContentType));
            Assert.False(string.IsNullOrWhiteSpace(exporter.FileExtension));
        }
    }
}
