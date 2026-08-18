using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Export;

/// CSV and TSV. One class for both: they differ only in the separator.
public sealed class DelimitedExporter(string format, string delimiter) : IResultExporter
{
    public string Format { get; } = format;
    public string Label => Format.ToUpperInvariant();
    public string ContentType => Format == "csv" ? "text/csv" : "text/tab-separated-values";
    public string FileExtension => Format;

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        // The delimiter of the format wins over the option unless the caller set a custom one.
        var separator = Format == "csv" ? options.Delimiter : delimiter;

        await using var writer = new StreamWriter(target, ExportEncoding.Resolve(options), leaveOpen: true)
        {
            AutoFlush = false,
        };

        var wroteHeader = false;

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns columns when options.Header && !wroteHeader:
                    wroteHeader = true;
                    await writer.WriteLineAsync(string.Join(separator,
                        columns.Items.Select(c => Escape(c.Name, separator, options))));
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items)
                        await writer.WriteLineAsync(string.Join(separator,
                            row.Select(v => Escape(ExportValue.ToText(v, options), separator, options))));
                    // Flush per chunk so a long export streams instead of buffering.
                    await writer.FlushAsync(ct);
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }

        await writer.FlushAsync(ct);
    }

    private static string Escape(string value, string separator, ExportOptions options)
    {
        var needsQuotes = options.QuoteAll
            || value.Contains(separator, StringComparison.Ordinal)
            || value.Contains('"') || value.Contains('\n') || value.Contains('\r');

        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

public sealed class ExportFailedException(string message) : Exception(message);

internal static class ExportEncoding
{
    public static Encoding Resolve(ExportOptions options) => options.Encoding.ToLowerInvariant() switch
    {
        "utf-8" or "utf8" => new UTF8Encoding(false),
        "utf-8-bom" => new UTF8Encoding(true),
        "utf-16" => Encoding.Unicode,
        "latin1" or "iso-8859-1" => Encoding.Latin1,
        _ => new UTF8Encoding(false),
    };
}

internal static class ExportValue
{
    /// The text form every text-based exporter shares, so CSV, Markdown and HTML never disagree
    /// about how a date or a NULL looks.
    public static string ToText(object? value, ExportOptions options) => value switch
    {
        null => options.NullText,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString(options.DateFormat),
        DateTimeOffset dto => dto.ToString(options.DateFormat),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? options.NullText,
    };
}
