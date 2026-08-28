using System.Globalization;
using MongoDB.Bson;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.MongoDb;

/// A page of documents, shown as rows.
///
/// The data tab asks every engine the same question — give me rows 200 to 400 of this thing, in this
/// order, filtered on that column — and for MongoDB the answer is a `find`, not a `SELECT`. The
/// documents are then projected onto the shape the driver sampled, so the grid gets a table while the
/// values keep their own kind: a nested object stays JSON in its cell rather than becoming the word
/// "Document".
public static class MongoPage
{
    /// A field the studio's own filter language can be translated for. Everything it cannot express
    /// in Mongo is said out loud rather than silently dropped.
    public static (BsonDocument Filter, string? Note) Filter(string column, string expression)
    {
        var text = expression.Trim();
        if (text.Length == 0 || column.Length == 0) return (new BsonDocument(), null);

        // A list of alternatives is what ticking values in the column menu writes.
        if (text.StartsWith('=') && text.Contains(",=", StringComparison.Ordinal))
        {
            var values = text.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.TrimStart('=').Trim())
                .Where(part => part.Length > 0)
                .Select(Value)
                .ToList();

            return (new BsonDocument(column, new BsonDocument("$in", new BsonArray(values))), null);
        }

        // The single-term forms, in the order the language documents them.
        if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return (new BsonDocument(column, BsonNull.Value), null);

        if (text.Equals("!NULL", StringComparison.OrdinalIgnoreCase)
            || text.Equals("NOT NULL", StringComparison.OrdinalIgnoreCase))
            return (new BsonDocument(column, new BsonDocument("$ne", BsonNull.Value)), null);

        if (text.StartsWith('^'))
            return (Regex(column, "^" + Escape(text[1..])), null);

        if (text.StartsWith('$'))
            return (Regex(column, Escape(text[1..]) + "$"), null);

        if (text.StartsWith('~'))
            return (new BsonDocument(column,
                new BsonDocument("$not", new BsonRegularExpression(Escape(text[1..]), "i"))), null);

        foreach (var (marker, op) in new[] { (">=", "$gte"), ("<=", "$lte"), ("!=", "$ne"),
                     (">", "$gt"), ("<", "$lt"), ("=", "$eq") })
            if (text.StartsWith(marker, StringComparison.Ordinal))
                return (new BsonDocument(column,
                    new BsonDocument(op, Value(text[marker.Length..].Trim()))), null);

        // A plain word means "contains", which is what it meant before this existed.
        if (text.StartsWith('+')) text = text[1..];

        return (Regex(column, Escape(text)),
            // The date periods — LAST MONTH, TODAY — are SQL-side sugar the driver does not translate.
            text.StartsWith("LAST ", StringComparison.OrdinalIgnoreCase)
            || text.Equals("TODAY", StringComparison.OrdinalIgnoreCase)
                ? $"'{expression}' was matched as text: MongoDB has no date periods here"
                : null);
    }

    private static BsonDocument Regex(string column, string pattern) =>
        new(column, new BsonRegularExpression(pattern, "i"));

    private static string Escape(string value) => System.Text.RegularExpressions.Regex.Escape(value);

    /// The value as the type it looks like: a number stays a number, so `>10` compares numerically
    /// rather than alphabetically.
    private static BsonValue Value(string text)
    {
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

        if (long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var whole))
            return whole;

        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            return number;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            return date;

        return text;
    }

    /// The documents as rows of the sampled shape. A field a document does not have is null, and a
    /// field the shape does not know about is not lost: it lands in the extra column the page adds
    /// when it finds one.
    public static (List<ColumnMeta> Columns, List<object?[]> Rows) Project(
        IReadOnlyList<BsonDocument> documents, IReadOnlyList<ColumnInfo> sampled)
    {
        var columns = sampled.Select(column => new ColumnMeta(column.Name, column.DataType, true))
            .ToList();

        var index = columns
            .Select((column, position) => (column.Name, position))
            .ToDictionary(entry => entry.Name, entry => entry.position, StringComparer.Ordinal);

        var rows = new List<object?[]>(documents.Count);
        var extra = new List<string>();

        foreach (var document in documents)
        {
            // A page may hold fields the sample never saw — documents have no schema, and that is
            // the whole point of showing them.
            foreach (var element in document.Elements)
                if (!index.ContainsKey(element.Name) && !extra.Contains(element.Name))
                    extra.Add(element.Name);
        }

        foreach (var name in extra)
        {
            index[name] = columns.Count;
            columns.Add(new ColumnMeta(name, "unsampled", true));
        }

        foreach (var document in documents)
        {
            var row = new object?[columns.Count];

            foreach (var element in document.Elements)
                if (index.TryGetValue(element.Name, out var position))
                    row[position] = Cell(element.Value);

            rows.Add(row);
        }

        return (columns, rows);
    }

    /// One value, as the grid can show it. A nested document or an array stays JSON — the column menu
    /// then offers "what is in this JSON", which is the same answer a JSONB column gets.
    private static object? Cell(BsonValue value) => value.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => null,
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Decimal128 => (decimal)value.AsDecimal128,
        BsonType.DateTime => value.ToUniversalTime(),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.String => value.AsString,
        BsonType.Document or BsonType.Array => value.ToJson(),
        BsonType.Binary => $"{value.AsBsonBinaryData.Bytes.Length} bytes",
        _ => value.ToString(),
    };
}
