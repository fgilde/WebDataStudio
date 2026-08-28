using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One path inside a JSON column, and what was found there.
public sealed record JsonPath(
    string Path,
    /// Every type seen at this path, in the order they turned up: `string`, `number`, `boolean`,
    /// `object`, `array`, `null`. More than one is worth knowing — that is where a flatten breaks.
    IReadOnlyList<string> Types,
    /// In how many of the sampled documents the path exists at all.
    int Present,
    string? Example);

public sealed record JsonShapeReport(
    int Sampled,
    int Parsed,
    IReadOnlyList<JsonPath> Paths,
    /// Why nothing could be read, where that is the answer.
    string? Note);

/// What is actually inside a JSON column.
///
/// A JSONB column is one cell of text in the grid: you can read one row of it and guess. This reads a
/// sample and says what the shape is — which paths exist, how often, with which types — so the column
/// can be filtered and flattened without guessing.
///
/// Sampled rather than exhaustive, and the report says how many rows it looked at: a shape derived
/// from two hundred documents is an honest answer, a full scan of a hundred million is not.
public static class JsonShape
{
    public const int DefaultSample = 200;

    /// How deep a path is followed. Deeper than this and the list stops being something to read.
    private const int MaxDepth = 6;

    /// Arrays collapse to one entry — `items[]` — because `items[0]`, `items[1]`… is a listing of a
    /// sample rather than a shape.
    private const string ArrayMarker = "[]";

    public static async Task<JsonShapeReport> DescribeAsync(
        IDbDriver driver, IDbSession session, string from, string column, int sample,
        CancellationToken ct)
    {
        var take = Math.Clamp(sample, 1, 5000);
        var quoted = driver.Dialect.QuoteIdentifier(column);

        // Only the column, only a page of it, and only rows that have something in it.
        var sql = driver.Dialect.Paginate(
            $"SELECT CAST({quoted} AS {driver.Dialect.TextType}) FROM {from} WHERE {quoted} IS NOT NULL",
            0, take);

        var documents = new List<string>();

        await using (var command = session.Connection.CreateCommand())
        {
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(0) && reader.GetValue(0)?.ToString() is { Length: > 0 } text)
                    documents.Add(text);
        }

        return Infer(documents);
    }

    /// The shape of a set of documents. Pure, so the interesting cases — a path that is a string in
    /// one row and a number in the next, an array of objects, a null — are tested without a server.
    public static JsonShapeReport Infer(IReadOnlyList<string> documents)
    {
        // Insertion order is remembered on purpose: within a level, the order the author wrote the
        // keys in reads better than alphabetical.
        var paths = new Dictionary<string, (List<string> Types, int Present, string? Example, int Order)>(
            StringComparer.Ordinal);
        var parsed = 0;

        foreach (var document in documents)
        {
            JsonDocument json;

            try
            {
                json = JsonDocument.Parse(document);
            }
            catch (JsonException)
            {
                // Not JSON at all. Counted by its absence from `Parsed`, which is what the panel
                // shows: "180 of 200 rows parsed".
                continue;
            }

            using (json)
            {
                parsed++;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                Walk(json.RootElement, "", 0, paths, seen);
            }
        }

        var report = paths
            // Parents before their children, and within a level the order they were first seen.
            .OrderBy(entry => entry.Key.Count(character => character == '.'))
            .ThenBy(entry => entry.Value.Order)
            .Select(entry => new JsonPath(entry.Key, entry.Value.Types, entry.Value.Present,
                entry.Value.Example))
            .ToList();

        return new JsonShapeReport(documents.Count, parsed, report,
            documents.Count == 0
                ? "nothing in this column to read"
                : parsed == 0 ? "none of the sampled rows is JSON" : null);
    }

    private static void Walk(JsonElement element, string path, int depth,
        Dictionary<string, (List<string> Types, int Present, string? Example, int Order)> paths,
        HashSet<string> seen)
    {
        if (depth > MaxDepth) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (path.Length > 0) Record(path, "object", null, paths, seen);

                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}",
                        depth + 1, paths, seen);
                break;

            case JsonValueKind.Array:
                Record(path, "array", null, paths, seen);

                // Every element of the array folds into one path: the shape of the items, not a
                // listing of them.
                foreach (var item in element.EnumerateArray())
                    Walk(item, path + ArrayMarker, depth + 1, paths, seen);
                break;

            default:
                Record(path, TypeOf(element), Example(element), paths, seen);
                break;
        }
    }

    private static void Record(string path, string type, string? example,
        Dictionary<string, (List<string> Types, int Present, string? Example, int Order)> paths,
        HashSet<string> seen)
    {
        if (path.Length == 0) return;

        if (!paths.TryGetValue(path, out var entry))
            entry = (new List<string>(), 0, null, paths.Count);

        if (!entry.Types.Contains(type)) entry.Types.Add(type);

        // Present counts documents, not occurrences: an array of fifty objects is one document that
        // has the path, otherwise `items[].name` would be "present 50 times in 1 row".
        if (seen.Add(path)) entry = (entry.Types, entry.Present + 1, entry.Example, entry.Order);

        paths[path] = (entry.Types, entry.Present, entry.Example ?? example, entry.Order);
    }

    private static string TypeOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };

    private static string? Example(JsonElement element)
    {
        var text = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ToString();

        if (text is null) return null;

        return text.Length <= 60 ? text : text[..60] + "…";
    }

    /// The SQL for one path, in this engine's own spelling. A flatten is then a SELECT of these, and
    /// a filter is one of them in a WHERE.
    public static string Expression(SqlDialect dialect, string column, string path) =>
        dialect.JsonPath(dialect.QuoteIdentifier(column), path);

    /// `SELECT col->>'a' AS a, … FROM x` — the flatten somebody wanted when they opened the column.
    ///
    /// Only the paths that hold a value: an object or an array has no single value to select, and a
    /// column of `{"name": "a"}` is the JSON somebody was trying to get out of.
    public static string FlattenSql(SqlDialect dialect, string from, string column,
        IReadOnlyList<JsonPath> paths)
    {
        var columns = paths
            .Where(path => path.Types.Any(type =>
                type is "string" or "number" or "boolean" or "null" or "unknown"))
            .Select(path =>
                $"{Expression(dialect, column, path.Path)} AS {dialect.QuoteIdentifier(Alias(path.Path))}")
            .ToList();

        return columns.Count == 0
            ? $"SELECT * FROM {from}"
            : $"SELECT {string.Join(", ", columns)}\n  FROM {from}";
    }

    /// `address.city` becomes `address_city`, which is a column name every engine accepts.
    internal static string Alias(string path) =>
        path.Replace(ArrayMarker, "").Replace('.', '_').Replace(' ', '_');
}
