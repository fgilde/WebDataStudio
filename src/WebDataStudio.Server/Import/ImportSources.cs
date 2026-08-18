using System.Globalization;
using System.Text;
using System.Text.Json;
using MiniExcelLibs;

namespace WebDataStudio.Server.Import;

public sealed record ImportSettings(bool HasHeader, string Delimiter, string Encoding, string? SheetName)
{
    public static ImportSettings Default { get; } = new(true, ",", "utf-8", null);
}

public sealed record ImportPreview(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string[]> SampleRows,
    IReadOnlyList<string> DetectedTypes);

public interface IImportSource
{
    string Format { get; }
    Task<ImportPreview> PreviewAsync(Stream input, ImportSettings settings, CancellationToken ct);
    IAsyncEnumerable<object?[]> ReadAsync(Stream input, ImportSettings settings, CancellationToken ct);
}

public static class ImportSources
{
    private const int SampleSize = 20;

    public static string? DetectFormat(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" or ".tsv" or ".txt" => "csv",
            ".xlsx" or ".xls" => "xlsx",
            ".json" or ".ndjson" => "json",
            ".sql" => "sql",
            _ => null,
        };

    public static IImportSource Get(string format) => format switch
    {
        "csv" => new CsvImportSource(),
        "xlsx" => new ExcelImportSource(),
        "json" => new JsonImportSource(),
        "sql" => new SqlScriptImportSource(),
        _ => throw new NotSupportedException($"no importer for format '{format}'"),
    };

    /// Names columns when the file has no header row.
    internal static string[] PositionalNames(int count) =>
        Enumerable.Range(1, count).Select(i => $"column{i}").ToArray();

    /// Guesses a column type from the sample, so the "create new table" path has something to
    /// propose. A guess, not a promise: the user can override every column in the dialog.
    internal static string[] DetectTypes(IReadOnlyList<string[]> sample, int columnCount)
    {
        var types = new string[columnCount];

        for (var c = 0; c < columnCount; c++)
        {
            var values = sample
                .Select(r => c < r.Length ? r[c] : null)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            types[c] = values.Count == 0 ? "text"
                : values.All(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) ? "integer"
                : values.All(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) ? "decimal"
                : values.All(v => bool.TryParse(v, out _)) ? "boolean"
                : values.All(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    ? (values.All(v => v!.Contains(':')) ? "timestamp" : "date")
                : "text";
        }

        return types;
    }

    internal static Encoding Resolve(ImportSettings settings) => settings.Encoding.ToLowerInvariant() switch
    {
        "utf-16" => Encoding.Unicode,
        "latin1" or "iso-8859-1" => Encoding.Latin1,
        _ => new UTF8Encoding(false),
    };

    internal static int Sample => SampleSize;
}

public sealed class CsvImportSource : IImportSource
{
    public string Format => "csv";

    public async Task<ImportPreview> PreviewAsync(Stream input, ImportSettings settings, CancellationToken ct)
    {
        var rows = new List<string[]>();
        string[]? header = null;

        await foreach (var fields in ReadFieldsAsync(input, settings, ct))
        {
            if (settings.HasHeader && header is null) { header = fields; continue; }
            rows.Add(fields);
            if (rows.Count >= ImportSources.Sample) break;
        }

        var width = header?.Length ?? rows.FirstOrDefault()?.Length ?? 0;
        var columns = header ?? ImportSources.PositionalNames(width);
        return new ImportPreview(columns, rows, ImportSources.DetectTypes(rows, columns.Length));
    }

    public async IAsyncEnumerable<object?[]> ReadAsync(Stream input, ImportSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var first = true;
        await foreach (var fields in ReadFieldsAsync(input, settings, ct))
        {
            if (settings.HasHeader && first) { first = false; continue; }
            first = false;
            yield return fields.Select(f => (object?)(f.Length == 0 ? null : f)).ToArray();
        }
    }

    /// A small RFC 4180 reader: quoted fields, doubled quotes, newlines inside quotes.
    private static async IAsyncEnumerable<string[]> ReadFieldsAsync(Stream input, ImportSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(input, ImportSources.Resolve(settings), leaveOpen: true);
        var delimiter = settings.Delimiter.Length > 0 ? settings.Delimiter[0] : ',';

        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var sawAny = false;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inQuotes)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else if (c == '"') inQuotes = false;
                    else current.Append(c);
                    continue;
                }

                if (c == '"') { inQuotes = true; sawAny = true; continue; }
                if (c == delimiter) { fields.Add(current.ToString()); current.Clear(); sawAny = true; continue; }
                current.Append(c);
                sawAny = true;
            }

            if (inQuotes) { current.Append('\n'); continue; }

            fields.Add(current.ToString());
            current.Clear();

            if (sawAny) yield return fields.ToArray();
            fields.Clear();
            sawAny = false;
        }
    }
}

public sealed class ExcelImportSource : IImportSource
{
    public string Format => "xlsx";

    public async Task<ImportPreview> PreviewAsync(Stream input, ImportSettings settings, CancellationToken ct)
    {
        var rows = new List<string[]>();
        string[]? header = null;

        await foreach (var row in RawAsync(input, settings, ct))
        {
            var text = row.Select(v => v?.ToString() ?? "").ToArray();
            if (settings.HasHeader && header is null) { header = text; continue; }
            rows.Add(text);
            if (rows.Count >= ImportSources.Sample) break;
        }

        var width = header?.Length ?? rows.FirstOrDefault()?.Length ?? 0;
        var columns = header ?? ImportSources.PositionalNames(width);
        return new ImportPreview(columns, rows, ImportSources.DetectTypes(rows, columns.Length));
    }

    public async IAsyncEnumerable<object?[]> ReadAsync(Stream input, ImportSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var first = true;
        await foreach (var row in RawAsync(input, settings, ct))
        {
            if (settings.HasHeader && first) { first = false; continue; }
            first = false;
            yield return row;
        }
    }

    private static async IAsyncEnumerable<object?[]> RawAsync(Stream input, ImportSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // MiniExcel's query is synchronous but lazy, so it never holds the whole sheet.
        foreach (var record in input.Query(useHeaderRow: false, sheetName: settings.SheetName))
        {
            ct.ThrowIfCancellationRequested();
            var values = ((IDictionary<string, object>)record).Values.ToArray();
            yield return values!;
        }
        await Task.CompletedTask;
    }
}

public sealed class JsonImportSource : IImportSource
{
    public string Format => "json";

    public async Task<ImportPreview> PreviewAsync(Stream input, ImportSettings settings, CancellationToken ct)
    {
        var (columns, rows) = await ReadDocumentAsync(input, ct);
        var sample = rows.Take(ImportSources.Sample)
            .Select(r => r.Select(v => v?.ToString() ?? "").ToArray())
            .ToList();

        return new ImportPreview(columns, sample, ImportSources.DetectTypes(sample, columns.Length));
    }

    public async IAsyncEnumerable<object?[]> ReadAsync(Stream input, ImportSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (_, rows) = await ReadDocumentAsync(input, ct);
        foreach (var row in rows) yield return row;
    }

    /// Column order comes from the first object; later objects fill their known keys and leave the
    /// rest null, so a missing key never shifts a row sideways.
    private static async Task<(string[] Columns, List<object?[]> Rows)> ReadDocumentAsync(
        Stream input, CancellationToken ct)
    {
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: ct);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToList()
            : [document.RootElement];

        var columns = elements.FirstOrDefault(e => e.ValueKind == JsonValueKind.Object)
            .EnumerateObject().Select(p => p.Name).ToArray();

        var rows = new List<object?[]>();
        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            var row = new object?[columns.Length];
            for (var i = 0; i < columns.Length; i++)
                row[i] = element.TryGetProperty(columns[i], out var value) ? Scalar(value) : null;
            rows.Add(row);
        }

        return (columns, rows);
    }

    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}

/// A dumped script: statements, not rows. Executed one at a time so progress is reportable and a
/// failure names the statement that broke rather than the whole file.
public sealed class SqlScriptImportSource : IImportSource
{
    public string Format => "sql";

    public async Task<ImportPreview> PreviewAsync(Stream input, ImportSettings settings, CancellationToken ct)
    {
        using var reader = new StreamReader(input, ImportSources.Resolve(settings), leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);

        var lines = text.Split('\n').Take(ImportSources.Sample).Select(l => new[] { l.TrimEnd('\r') }).ToList();
        return new ImportPreview(["statement"], lines, ["text"]);
    }

    public async IAsyncEnumerable<object?[]> ReadAsync(Stream input, ImportSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(input, ImportSources.Resolve(settings), leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        yield return [text];
    }
}
