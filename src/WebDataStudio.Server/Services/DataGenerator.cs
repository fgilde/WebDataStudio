using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Editing;

namespace WebDataStudio.Server.Services;

public sealed record GenerateRequest(
    string ObjectRef, int Rows, IReadOnlyDictionary<string, string>? Strategies, int? Seed);

/// Plausible rows for an empty table: enough to see a screen full of data, click through a
/// foreign key and have a query return something.
///
/// Every value comes from a seeded `Random`, so the same seed produces the same rows — which is
/// what makes generated data usable in a bug report.
public static partial class DataGenerator
{
    public static readonly string[] Strategies =
    [
        "auto", "name", "email", "city", "sentence", "int", "decimal", "date", "uuid", "boolean",
        "fk", "skip",
    ];

    private static readonly string[] FirstNames =
    [
        "Ada", "Grace", "Linus", "Alan", "Barbara", "Edsger", "Katherine", "Dennis", "Margaret",
        "Ken", "Radia", "Niklaus", "Frances", "Guido", "Anita", "Tim",
    ];

    private static readonly string[] LastNames =
    [
        "Lovelace", "Hopper", "Torvalds", "Turing", "Liskov", "Dijkstra", "Johnson", "Ritchie",
        "Hamilton", "Thompson", "Perlman", "Wirth", "Allen", "Rossum", "Borg", "Berners-Lee",
    ];

    private static readonly string[] Cities =
    [
        "London", "Helsinki", "Lisbon", "Zurich", "Tokyo", "Nairobi", "Bogotá", "Reykjavík",
        "Hanoi", "Toronto", "Vienna", "Cape Town",
    ];

    private static readonly string[] Words =
    [
        "small", "green", "quiet", "broken", "second", "orange", "friendly", "cold", "quick",
        "table", "index", "cursor", "report", "invoice", "region", "session", "cache", "column",
    ];

    [GeneratedRegex(@"\((\d+)")]
    private static partial Regex LengthOf();

    /// What a column looks like it holds, from its name first and its type second. The name is the
    /// better signal: a `city` column is a `varchar` like any other.
    public static string Infer(ColumnInfo column, ObjectDetail detail)
    {
        if (detail.ForeignKeys.Any(fk => fk.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase)))
            return "fk";

        var name = column.Name.ToLowerInvariant();
        var type = column.DataType.ToLowerInvariant();

        // A key the database fills in itself stays out of the insert. Only some drivers report the
        // identity flag, so a lone integer primary key counts too: that is a serial, an
        // AUTO_INCREMENT, an IDENTITY or a SQLite rowid alias in every engine the studio speaks. If
        // an engine really wants the value, it says so — and the insert was previewed first.
        var soleKey = column.IsPrimaryKey
            && detail.Columns.Count(c => c.IsPrimaryKey) == 1
            && (type.Contains("int") || type.Contains("serial"));

        if (column.IsIdentity || soleKey) return "skip";

        if (name.Contains("email") || name.Contains("mail")) return "email";
        if (name.Contains("city") || name.Contains("town")) return "city";
        if (name is "name" || name.EndsWith("name") || name.Contains("author")) return "name";
        if (name.Contains("uuid") || name.Contains("guid")) return "uuid";
        if (name.Contains("comment") || name.Contains("description") || name.Contains("note")
            || name.Contains("text") || name.Contains("title"))
            return "sentence";

        if (type.Contains("bool") || type == "bit") return "boolean";
        if (type.Contains("uuid") || type.Contains("guid")) return "uuid";
        if (type.Contains("date") || type.Contains("time")) return "date";
        if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money")
            || type.Contains("double") || type.Contains("real") || type.Contains("float"))
            return "decimal";
        if (type.Contains("int") || type.Contains("serial")) return "int";

        return "sentence";
    }

    /// The rows to insert, as the same change objects the grid's editor produces — so generated
    /// data goes through the preview and apply everything else goes through.
    ///
    /// `parents` holds the values a foreign key may point at, per column; a foreign key with no
    /// parent rows to point at is left null when it may be, and the column is skipped when it may
    /// not, because inventing a key would break the constraint the column exists for.
    public static List<RowChange> Build(
        ObjectDetail detail, GenerateRequest request,
        IReadOnlyDictionary<string, IReadOnlyList<object?>> parents)
    {
        var random = new Random(request.Seed ?? 20260821);
        var unique = UniqueColumns(detail);
        var changes = new List<RowChange>();

        for (var row = 0; row < request.Rows; row++)
        {
            var values = new Dictionary<string, object?>();

            foreach (var column in detail.Columns.OrderBy(c => c.Position))
            {
                var strategy = request.Strategies is not null
                    && request.Strategies.TryGetValue(column.Name, out var chosen)
                    && Strategies.Contains(chosen)
                        ? chosen
                        : Infer(column, detail);

                if (strategy == "skip") continue;

                if (strategy == "fk")
                {
                    if (!parents.TryGetValue(column.Name, out var candidates) || candidates.Count == 0)
                    {
                        // Nothing to point at: a null if the column allows one, otherwise leave it
                        // out and let the database say what it needs.
                        if (column.Nullable) values[column.Name] = null;
                        continue;
                    }

                    values[column.Name] = candidates[random.Next(candidates.Count)];
                    continue;
                }

                var value = Generate(strategy, random, row, unique.Contains(column.Name));
                values[column.Name] = Fit(value, column);
            }

            if (values.Count > 0)
                changes.Add(new RowChange("insert", new Dictionary<string, object?>(), values));
        }

        return changes;
    }

    /// Columns that have to differ per row: the primary key, and anything a unique index covers on
    /// its own. A composite unique index is left alone — its columns only have to differ together.
    private static HashSet<string> UniqueColumns(ObjectDetail detail)
    {
        var unique = new HashSet<string>(
            detail.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var index in detail.Indexes.Where(i => i.Unique && i.Columns.Count == 1))
            unique.Add(index.Columns[0]);

        return unique;
    }

    private static object? Generate(string strategy, Random random, int row, bool unique) => strategy switch
    {
        "name" => $"{Pick(FirstNames, random)} {Pick(LastNames, random)}",
        "email" => unique
            ? $"{Pick(FirstNames, random).ToLowerInvariant()}.{row}@example.com"
            : $"{Pick(FirstNames, random).ToLowerInvariant()}.{Pick(LastNames, random).ToLowerInvariant()}@example.com",
        "city" => Pick(Cities, random),
        "sentence" => Sentence(random, unique ? row : null),
        "int" => unique ? row + 1 : random.Next(1, 10_000),
        "decimal" => Math.Round(random.NextDouble() * 1000, 2),
        // Both from the seed, not from the clock: "the same seed produces the same rows" has to hold
        // tomorrow as well, or a generated dataset cannot be talked about in a bug report.
        "date" => Epoch.AddDays(-random.Next(0, 720)).ToString("yyyy-MM-dd"),
        "uuid" => DeterministicGuid(random).ToString(),
        "boolean" => random.Next(2) == 1,
        _ => Sentence(random, unique ? row : null),
    };

    /// Respects what the type says it can hold, so a `varchar(10)` does not fail on the first row.
    private static object? Fit(object? value, ColumnInfo column)
    {
        if (value is not string text) return value;

        var match = LengthOf().Match(column.DataType);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var length) || length <= 0)
            return text;

        return text.Length <= length ? text : text[..length];
    }

    private static string Sentence(Random random, int? suffix)
    {
        var words = Enumerable.Range(0, random.Next(3, 8)).Select(_ => Pick(Words, random));
        var text = string.Join(" ", words);
        return suffix is null ? text : $"{text} {suffix}";
    }

    private static string Pick(string[] from, Random random) => from[random.Next(from.Length)];

    /// Date values are counted back from here rather than from today.
    private static readonly DateTime Epoch = new(2026, 1, 1);

    private static Guid DeterministicGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}
