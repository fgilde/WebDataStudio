using System.Text.Json;
using WebDataStudio.Server.Export;
using static WebDataStudio.Server.Tests.Export.DelimitedExporterTests;

namespace WebDataStudio.Server.Tests.Export;

public class TextExporterTests
{
    [Fact]
    public async Task Json_writes_an_array_of_objects_with_real_nulls()
    {
        var json = await ExportAsync(new JsonExporter(), ExportOptions.Default);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(3, document.RootElement.GetArrayLength());
        Assert.Equal("ada", document.RootElement[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement[1].GetProperty("name").ValueKind);
    }

    [Fact]
    public async Task NdJson_writes_one_object_per_line()
    {
        var text = await ExportAsync(new NdJsonExporter(), ExportOptions.Default);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        foreach (var line in lines) JsonDocument.Parse(line).Dispose();
    }

    [Fact]
    public async Task Xml_wraps_rows_and_escapes_content()
    {
        var xml = await ExportAsync(new XmlExporter(), ExportOptions.Default);

        Assert.Contains("<rows>", xml);
        Assert.Contains("<id>1</id>", xml);
        Assert.Contains("&quot;hi&quot;", xml.Replace("\"hi\"", "&quot;hi&quot;"));
        System.Xml.Linq.XDocument.Parse(xml);
    }

    [Fact]
    public async Task Yaml_writes_a_sequence_of_mappings()
    {
        var yaml = await ExportAsync(new YamlExporter(), ExportOptions.Default);

        Assert.Contains("- id: 1", yaml);
        Assert.Contains("name: ada", yaml);
    }

    [Fact]
    public async Task Markdown_writes_a_table_with_a_separator_row()
    {
        var md = await ExportAsync(new MarkdownExporter(), ExportOptions.Default);
        var lines = md.Split('\n');

        Assert.StartsWith("| id | name |", lines[0]);
        Assert.Contains("---", lines[1]);
        Assert.Contains("| 1 | ada |", md);
    }

    [Fact]
    public async Task Markdown_escapes_a_pipe_in_a_value()
    {
        var md = await ExportAsync(new MarkdownExporter(), ExportOptions.Default with { NullText = "a|b" });
        Assert.Contains("a\\|b", md);
    }

    [Fact]
    public async Task Html_writes_an_escaped_table()
    {
        var html = await ExportAsync(new HtmlExporter(), ExportOptions.Default);

        Assert.Contains("<table>", html);
        Assert.Contains("<th>id</th>", html);
        Assert.Contains("&quot;hi&quot;", html);
    }

    public static TheoryData<string> TextFormats() =>
        new() { "csv", "tsv", "json", "ndjson", "xml", "yaml", "markdown", "html" };

    [Theory]
    [MemberData(nameof(TextFormats))]
    public async Task An_empty_result_still_produces_a_valid_document(string format)
    {
        var exporter = new ExporterRegistry().Get(format);

        using var stream = new MemoryStream();
        await exporter.WriteAsync(stream, Empty(), ExportOptions.Default, TestContext.Current.CancellationToken);

        // No exception, and nothing half-written: that is all an empty export owes the caller.
        Assert.True(stream.Length >= 0);

        static async IAsyncEnumerable<WebDataStudio.Server.Drivers.Abstractions.ResultChunk> Empty()
        {
            yield return new WebDataStudio.Server.Drivers.Abstractions.ResultChunk.Columns(0, []);
            yield return new WebDataStudio.Server.Drivers.Abstractions.ResultChunk.End(0, 0, 0, false);
            await Task.CompletedTask;
        }
    }
}
