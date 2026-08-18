using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace WebDataStudio.Server.Drivers.MongoDb;

public sealed record MongoCommand(
    string Collection, string Operation, IReadOnlyList<BsonDocument> Arguments,
    int? Limit, int? Skip, BsonDocument? Sort)
{
    private static readonly HashSet<string> Writes = new(StringComparer.OrdinalIgnoreCase)
    {
        "insertOne", "insertMany", "updateOne", "updateMany", "replaceOne",
        "deleteOne", "deleteMany", "drop", "createIndex", "dropIndex", "renameCollection",
    };

    public bool IsWrite => Writes.Contains(Operation);
}

/// Parses the shell syntax people actually type: db.people.find({ active: true }).limit(10).
/// Relaxed JSON is accepted because that is what the shell accepts.
public static partial class MongoCommandParser
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "find", "findOne", "aggregate", "countDocuments", "estimatedDocumentCount", "distinct",
        "insertOne", "insertMany", "updateOne", "updateMany", "replaceOne", "deleteOne", "deleteMany",
        "createIndex", "getIndexes", "dropIndex", "drop",
    };

    public static MongoCommand Parse(string text)
    {
        var trimmed = text.Trim().TrimEnd(';').Trim();

        var match = CommandPattern().Match(trimmed);
        if (!match.Success)
            throw new FormatException(
                "expected something like db.collection.find({ ... }); this is not a MongoDB command");

        var collection = match.Groups["collection"].Value;
        var operation = match.Groups["operation"].Value;

        if (!Known.Contains(operation))
            throw new NotSupportedException($"operation '{operation}' is not supported");

        // The regex only finds where the argument list starts; its end has to be located by
        // counting parentheses, or a trailing .limit(10) would be swallowed into the arguments.
        var open = match.Index + match.Length - 1;
        var close = MatchingParen(trimmed, open);
        if (close < 0) throw new FormatException("the argument list is not closed");

        var arguments = ParseArguments(trimmed[(open + 1)..close]);

        // Trailing modifiers: .limit(10).skip(5).sort({ name: 1 })
        var rest = trimmed[(close + 1)..];
        int? limit = null;
        int? skip = null;
        BsonDocument? sort = null;

        foreach (Match modifier in ModifierPattern().Matches(rest))
        {
            var value = modifier.Groups["value"].Value.Trim();
            switch (modifier.Groups["name"].Value.ToLowerInvariant())
            {
                case "limit" when int.TryParse(value, out var l): limit = l; break;
                case "skip" when int.TryParse(value, out var s): skip = s; break;
                case "sort": sort = ParseDocument(value); break;
            }
        }

        return new MongoCommand(collection, operation, arguments, limit, skip, sort);
    }

    private static IReadOnlyList<BsonDocument> ParseArguments(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return [];

        // An aggregation pipeline arrives as an array of stages.
        if (text.StartsWith('['))
            return BsonSerializer.DeserializeArray(text);

        var documents = new List<BsonDocument>();
        foreach (var part in SplitTopLevel(text)) documents.Add(ParseDocument(part));
        return documents;
    }

    private static BsonDocument ParseDocument(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return [];

        try { return BsonDocument.Parse(trimmed); }
        catch (Exception e)
        {
            throw new FormatException($"could not read '{trimmed}' as a document: {e.Message}");
        }
    }

    /// Splits "{...}, {...}" into its documents without tripping over nested braces or strings.
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        var inString = false;
        char quote = '"';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (c == quote && text[i - 1] != '\\') inString = false;
                continue;
            }

            switch (c)
            {
                case '"' or '\'': inString = true; quote = c; break;
                case '{' or '[': depth++; break;
                case '}' or ']': depth--; break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }

        if (start < text.Length) yield return text[start..];
    }

    /// Finds the head of the command; the argument list is delimited by MatchingParen below.
    [GeneratedRegex(@"^db\s*\.\s*(?<collection>[A-Za-z_][\w.$-]*)\s*\.\s*(?<operation>[A-Za-z]+)\s*\(")]
    private static partial Regex CommandPattern();

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        var inString = false;
        var quote = '"';

        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (c == quote && text[i - 1] != '\\') inString = false;
                continue;
            }

            switch (c)
            {
                case '"' or '\'': inString = true; quote = c; break;
                case '(': depth++; break;
                case ')':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"\.\s*(?<name>limit|skip|sort)\s*\(\s*(?<value>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ModifierPattern();
}

internal static class BsonSerializer
{
    public static List<BsonDocument> DeserializeArray(string json)
    {
        var array = BsonSerializerHelper.ParseArray(json);
        return array.Select(v => v.AsBsonDocument).ToList();
    }
}

internal static class BsonSerializerHelper
{
    /// BsonDocument.Parse only reads objects, so an array is wrapped and unwrapped again.
    public static BsonArray ParseArray(string json) =>
        BsonDocument.Parse($"{{ \"a\": {json} }}")["a"].AsBsonArray;
}
