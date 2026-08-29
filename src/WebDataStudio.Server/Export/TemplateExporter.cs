using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Export;

/// An export format somebody wrote themselves.
///
/// DataGrip calls these extractors and writes them in Groovy, which means an export format is a
/// program the studio would have to run. This is the same idea without that: three pieces of text
/// with placeholders, no code, nothing to execute.
///
/// ```
/// header: INSERT INTO {{table}} ({{columns}}) VALUES
/// row:    ({{values}}){{comma}}
/// footer: ;
/// ```
///
/// Placeholders: `{{table}}`, `{{columns}}`, `{{values}}`, `{{index}}`, `{{comma}}` — a comma on
/// every row but the last — and `{{col.NAME}}` for one column by name. Each of them takes a filter:
/// `{{values|json}}`, `{{col.name|sql}}`, `{{col.note|html}}`, `{{col.note|csv}}`.
public sealed record ExportTemplate(
    string Id,
    string Label,
    string Extension,
    string ContentType,
    string? Header,
    string Row,
    string? Footer,
    /// What joins the columns and values of `{{columns}}` and `{{values}}`.
    string Separator = ", ");

public sealed class TemplateExporter(ExportTemplate template) : IResultExporter
{
    public ExportTemplate Template { get; } = template;

    public string Format => $"template:{Template.Id}";
    public string Label => Template.Label;
    public string ContentType => Template.ContentType;
    public string FileExtension => Template.Extension;

    /// The relaxed encoder: a quote inside an exported string should read as \" and not as ",
    /// which is valid JSON but unreadable in a file somebody opens.
    private static readonly JsonSerializerOptions JsonText = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string CommaMarker = "\u0001comma\u0001";

    private static readonly Regex Placeholder =
        new(@"\{\{\s*(?<name>[a-zA-Z0-9_.]+)\s*(?:\|\s*(?<filter>[a-z]+)\s*)?\}\}",
            RegexOptions.Compiled);

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        await using var writer = new StreamWriter(target, ExportEncoding.Resolve(options), leaveOpen: true)
        {
            AutoFlush = false,
        };

        var columns = new List<string>();
        var wroteHeader = false;
        var index = 0;

        // The row template may end in {{comma}}, which is only known once the next row arrives — so
        // a row is held back until then. One row of delay, not the whole result.
        string? pending = null;

        async Task FlushPending(bool last)
        {
            if (pending is null) return;

            await writer.WriteAsync(pending.Replace(CommaMarker, last ? "" : ","));
            pending = null;
        }

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns names:
                    columns.Clear();
                    columns.AddRange(names.Items.Select(column => column.Name));

                    if (!wroteHeader && Template.Header is { } header)
                    {
                        wroteHeader = true;
                        await writer.WriteAsync(Render(header, columns, null, options, 0));
                    }

                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items)
                    {
                        await FlushPending(false);
                        pending = Render(Template.Row, columns, row, options, ++index);
                    }

                    await writer.FlushAsync(ct);
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }

        await FlushPending(true);

        if (Template.Footer is { } footer)
            await writer.WriteAsync(Render(footer, columns, null, options, index));

        await writer.FlushAsync(ct);
    }

    private string Render(string text, IReadOnlyList<string> columns, IReadOnlyList<object?>? row,
        ExportOptions options, int index) =>
        Placeholder.Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            var filter = match.Groups["filter"].Success ? match.Groups["filter"].Value : null;

            if (name.StartsWith("col.", StringComparison.OrdinalIgnoreCase))
            {
                var column = name[4..];
                var at = IndexOf(columns, column);

                // A template that names a column the result does not have says so in the output
                // rather than failing the export halfway through a file.
                if (at < 0) return $"{{{{unknown column {column}}}}}";

                return Filter(Value(row, at, options), filter, options);
            }

            return name.ToLowerInvariant() switch
            {
                "table" => options.TableName ?? "result",
                "columns" => string.Join(Template.Separator,
                    columns.Select(column => Filter(column, filter, options))),
                "values" => row is null
                    ? ""
                    : string.Join(Template.Separator,
                        Enumerable.Range(0, columns.Count)
                            .Select(at => Filter(Value(row, at, options), filter, options))),
                "index" => index.ToString(),
                // Replaced when the next row shows up, or dropped on the last one.
                "comma" => CommaMarker,
                _ => match.Value,
            };
        });

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var at = 0; at < columns.Count; at++)
            if (columns[at].Equals(name, StringComparison.OrdinalIgnoreCase)) return at;

        return -1;
    }

    private static string Value(IReadOnlyList<object?>? row, int at, ExportOptions options) =>
        row is null || at >= row.Count ? options.NullText : ExportValue.ToText(row[at], options);

    private static string Filter(string value, string? filter, ExportOptions options) =>
        filter switch
        {
            "json" => JsonSerializer.Serialize(value, JsonText),
            // The dialect's own quoting where there is one, so a template can produce SQL that runs
            // on the engine it was exported for.
            "sql" => options.Dialect?.QuoteLiteral(value) ?? "'" + value.Replace("'", "''") + "'",
            "csv" => value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value,
            "html" => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;"),
            "upper" => value.ToUpperInvariant(),
            "lower" => value.ToLowerInvariant(),
            _ => value,
        };
}

/// Where the templates come from: a folder the deployment mounts, and whatever somebody saved in
/// this studio. A template is data, so both are just text.
public sealed class ExportTemplates
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<ExportTemplate> _fromDisk = [];
    private readonly Func<string?> _stored;
    private readonly Action<string> _save;

    public string? Error { get; }

    public ExportTemplates(IConfiguration config, Func<string?> stored, Action<string> save)
    {
        _stored = stored;
        _save = save;

        if (config["WDS_EXPORT_TEMPLATES_DIR"] is not { Length: > 0 } directory) return;

        try
        {
            // One setting, one or more folders — a repository's templates and an app host's own.
            foreach (var file in ConfiguredPaths.Files(directory, "*.json", SearchOption.TopDirectoryOnly))
                if (JsonSerializer.Deserialize<ExportTemplate>(File.ReadAllText(file), Json)
                    is { } template)
                    _fromDisk.Add(template);
        }
        catch (Exception e)
        {
            // A bad template must not stop the studio from starting; the message is reported where
            // the templates are listed.
            Error = e.Message;
        }
    }

    /// Everything available, the studio's own last so a mounted template of the same id wins.
    public IReadOnlyList<ExportTemplate> All()
    {
        var stored = _stored() is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<ExportTemplate>>(json, Json) ?? []
            : [];

        var byId = new Dictionary<string, ExportTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in stored) byId[template.Id] = template;
        foreach (var template in _fromDisk) byId[template.Id] = template;

        return byId.Values.OrderBy(template => template.Label).ToList();
    }

    public ExportTemplate? Find(string id) =>
        All().FirstOrDefault(template => template.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// Saves a template in this studio. A mounted one cannot be edited here: it belongs to the
    /// deployment, and a copy under another name is the honest way to change it.
    public void Save(ExportTemplate template)
    {
        if (_fromDisk.Any(existing => existing.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"'{template.Id}' comes from WDS_EXPORT_TEMPLATES_DIR; save it under another id");

        var stored = _stored() is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<ExportTemplate>>(json, Json) ?? []
            : [];

        stored.RemoveAll(existing => existing.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase));
        stored.Add(template);

        _save(JsonSerializer.Serialize(stored, Json));
    }

    public void Delete(string id)
    {
        var stored = _stored() is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<ExportTemplate>>(json, Json) ?? []
            : [];

        stored.RemoveAll(existing => existing.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        _save(JsonSerializer.Serialize(stored, Json));
    }
}
