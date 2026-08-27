namespace WebDataStudio.Server.Export;

public sealed class ExporterRegistry
{
    private readonly Dictionary<string, IResultExporter> _exporters;
    private readonly ExportTemplates? _templates;

    /// The templates are optional: everything here works without them, and a studio that has none
    /// simply offers the built-in formats.
    public ExporterRegistry(ExportTemplates? templates = null)
    {
        _templates = templates;

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

    public IReadOnlyCollection<IResultExporter> All() =>
    [
        .. _exporters.Values,
        // A template is a format like any other from here on: the dialog lists it, the endpoint
        // streams it, and nothing else needs to know it was written by a person.
        .. (_templates?.All() ?? []).Select(template => new TemplateExporter(template)),
    ];

    public IResultExporter Get(string format)
    {
        if (format.StartsWith(TemplatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = format[TemplatePrefix.Length..];

            return _templates?.Find(id) is { } template
                ? new TemplateExporter(template)
                : throw new NotSupportedException($"no export template '{id}'");
        }

        return _exporters.TryGetValue(format, out var exporter)
            ? exporter
            : throw new NotSupportedException($"no exporter for format '{format}'");
    }

    private const string TemplatePrefix = "template:";
}
