using System.Net;
using System.Text;
using System.Xml;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Export;

public sealed class XmlExporter : IResultExporter
{
    public string Format => "xml";
    public string Label => "XML";
    public string ContentType => "application/xml";
    public string FileExtension => "xml";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        var settings = new XmlWriterSettings
        {
            Async = true, Indent = true, Encoding = ExportEncoding.Resolve(options), CloseOutput = false,
        };
        await using var writer = XmlWriter.Create(target, settings);

        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "rows", null);

        IReadOnlyList<ColumnMeta> columns = [];

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns = c.Items;
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items)
                    {
                        await writer.WriteStartElementAsync(null, "row", null);
                        for (var i = 0; i < row.Length; i++)
                        {
                            var name = ElementName(i < columns.Count ? columns[i].Name : $"column{i + 1}");
                            await writer.WriteElementStringAsync(null, name, null,
                                ExportValue.ToText(row[i], options));
                        }
                        await writer.WriteEndElementAsync();
                    }
                    await writer.FlushAsync();
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    /// Column names are free text; element names are not. Anything illegal becomes an underscore.
    private static string ElementName(string name)
    {
        var cleaned = new string(name.Select(c => XmlConvert.IsNCNameChar(c) ? c : '_').ToArray());
        return cleaned.Length == 0 || !XmlConvert.IsStartNCNameChar(cleaned[0]) ? "_" + cleaned : cleaned;
    }
}

public sealed class YamlExporter : IResultExporter
{
    public string Format => "yaml";
    public string Label => "YAML";
    public string ContentType => "application/yaml";
    public string FileExtension => "yaml";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        await using var writer = new StreamWriter(target, ExportEncoding.Resolve(options), leaveOpen: true)
        {
            AutoFlush = false,
        };

        IReadOnlyList<ColumnMeta> columns = [];

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns = c.Items;
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items)
                    {
                        for (var i = 0; i < row.Length; i++)
                        {
                            var name = i < columns.Count ? columns[i].Name : $"column{i + 1}";
                            var prefix = i == 0 ? "- " : "  ";
                            await writer.WriteLineAsync($"{prefix}{name}: {Scalar(row[i], options)}");
                        }
                    }
                    await writer.FlushAsync(ct);
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }

        await writer.FlushAsync(ct);
    }

    private static string Scalar(object? value, ExportOptions options)
    {
        if (value is null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong
                  or float or double or decimal)
            return ExportValue.ToText(value, options);

        var text = ExportValue.ToText(value, options);
        // Quote anything that YAML would otherwise read as structure, a number or a keyword.
        var needsQuotes = text.Length == 0
            || text.IndexOfAny([':', '#', '-', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`', '"', '\'', '\n']) >= 0
            || double.TryParse(text, out _)
            || text is "true" or "false" or "null" or "yes" or "no";

        return needsQuotes ? "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"" : text;
    }
}

public sealed class MarkdownExporter : IResultExporter
{
    public string Format => "markdown";
    public string Label => "Markdown";
    public string ContentType => "text/markdown";
    public string FileExtension => "md";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        await using var writer = new StreamWriter(target, ExportEncoding.Resolve(options), leaveOpen: true)
        {
            AutoFlush = false,
        };

        var wroteHeader = false;

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c when !wroteHeader:
                    wroteHeader = true;
                    if (c.Items.Count == 0) break;
                    // The header goes out before any row arrives, so widths cannot be measured
                    // across the whole result — a fixed separator keeps the table streamable.
                    await writer.WriteLineAsync($"| {string.Join(" | ", c.Items.Select(x => Cell(x.Name)))} |");
                    await writer.WriteLineAsync($"|{string.Concat(c.Items.Select(_ => " --- |"))}");
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items)
                        await writer.WriteLineAsync(
                            $"| {string.Join(" | ", row.Select(v => Cell(ExportValue.ToText(v, options))))} |");
                    await writer.FlushAsync(ct);
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }

        await writer.FlushAsync(ct);
    }

    private static string Cell(string value) =>
        value.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
}

public sealed class HtmlExporter : IResultExporter
{
    public string Format => "html";
    public string Label => "HTML";
    public string ContentType => "text/html";
    public string FileExtension => "html";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        await using var writer = new StreamWriter(target, ExportEncoding.Resolve(options), leaveOpen: true)
        {
            AutoFlush = false,
        };

        await writer.WriteLineAsync("<!doctype html><meta charset=\"utf-8\"><table>");

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c when options.Header && c.Items.Count > 0:
                    await writer.WriteLineAsync(
                        $"<thead><tr>{string.Concat(c.Items.Select(x => $"<th>{WebUtility.HtmlEncode(x.Name)}</th>"))}</tr></thead>");
                    break;

                case ResultChunk.Rows rows:
                    var buffer = new StringBuilder();
                    foreach (var row in rows.Items)
                        buffer.Append("<tr>")
                              .Append(string.Concat(row.Select(v =>
                                  $"<td>{WebUtility.HtmlEncode(ExportValue.ToText(v, options))}</td>")))
                              .AppendLine("</tr>");
                    await writer.WriteAsync(buffer.ToString());
                    await writer.FlushAsync(ct);
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }

        await writer.WriteLineAsync("</table>");
        await writer.FlushAsync(ct);
    }
}
