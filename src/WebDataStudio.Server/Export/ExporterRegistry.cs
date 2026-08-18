namespace WebDataStudio.Server.Export;

public sealed class ExporterRegistry
{
    private readonly Dictionary<string, IResultExporter> _exporters;

    public ExporterRegistry()
    {
        IResultExporter[] exporters =
        [
            new DelimitedExporter("csv", ","),
            new DelimitedExporter("tsv", "\t"),
            new JsonExporter(),
            new NdJsonExporter(),
            new XmlExporter(),
            new YamlExporter(),
            new MarkdownExporter(),
            new HtmlExporter(),
            new ExcelExporter(),
            new ParquetExporter(),
            new SqlInsertExporter(),
            new SqlSchemaExporter(),
        ];
        _exporters = exporters.ToDictionary(e => e.Format, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IResultExporter> All() => _exporters.Values;

    public IResultExporter Get(string format) =>
        _exporters.TryGetValue(format, out var exporter)
            ? exporter
            : throw new NotSupportedException($"no exporter for format '{format}'");
}
