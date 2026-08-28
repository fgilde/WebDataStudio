using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// What one column actually holds, counted rather than guessed.
public sealed record ColumnStat(
    string Name,
    string DataType,
    long Rows,
    /// Rows where the column has a value.
    long NonNull,
    /// How many different values, where the engine could be asked. Null for a type it refuses to
    /// group by — a blob, a geometry.
    long? Distinct,
    string? Min,
    string? Max)
{
    public long Nulls => Rows - NonNull;

    public double NullPercent => Rows == 0 ? 0 : Math.Round(100.0 * Nulls / Rows, 1);

    /// Every value different, and none missing: this column identifies a row whether or not anybody
    /// declared that.
    public bool Unique => Rows > 0 && Nulls == 0 && Distinct == Rows;

    /// One value for every row and nothing else — a column somebody forgot to drop.
    public bool Constant => Rows > 1 && Distinct == 1;
}

/// A column that looks like it holds something personal, from the values rather than from the name.
public sealed record SensitiveHint(string Column, string Looks, int Matches, int Sampled)
{
    public double Percent => Sampled == 0 ? 0 : Math.Round(100.0 * Matches / Sampled, 1);
}

/// A rule the profile suggests, in the shape the data quality tab saves.
public sealed record ProfileSuggestion(string Column, QualityKind Kind, string? Argument, string Why);

/// The numbers behind a table, and what they suggest.
///
/// The health report reads the catalogue and the quality rules count what breaks them; between the
/// two there is the question both assume somebody has already answered — what is *in* this column.
/// One aggregate per column in a single statement answers it: how many rows, how many of them have a
/// value, how many different ones, the smallest and the largest.
///
/// The masking heuristic reads column *names*: `api_key` is a secret, `password_changed_at` is a
/// timestamp. That misses `col_17`, and this is the other half — a sample of the values, matched
/// against the shapes an IBAN, a card number, an address or a phone number have.
public static partial class ColumnProfile
{
    /// Columns per statement. A table with three hundred columns would otherwise build a statement
    /// no engine wants to parse, and the answer says how many were left out.
    public const int MaxColumns = 60;

    /// Rows read for the pattern check. Enough to be sure, small enough to be free.
    public const int DefaultSample = 200;

    /// The one statement that counts every column. `count(*)` once, then three aggregates per
    /// column — engines optimise this into a single pass.
    public static string CountSql(SqlDialect dialect, string from, IReadOnlyList<ColumnMeta> columns)
    {
        var sql = new StringBuilder("SELECT count(*) AS wds_rows");

        for (var i = 0; i < columns.Count; i++)
        {
            var column = dialect.QuoteIdentifier(columns[i].Name);

            sql.Append(CultureInfo.InvariantCulture, $", count({column}) AS wds_n{i}");

            // A type the engine cannot group by answers with an error for the whole statement, so
            // the distinct count is asked only where it can be.
            if (Groupable(columns[i].DataType))
                sql.Append(CultureInfo.InvariantCulture, $", count(DISTINCT {column}) AS wds_d{i}");

            if (Comparable(columns[i].DataType))
                sql.Append(CultureInfo.InvariantCulture,
                    $", min({column}) AS wds_lo{i}, max({column}) AS wds_hi{i}");
        }

        return sql.Append(" FROM ").Append(from).ToString();
    }

    /// The rows the pattern check reads. Text columns only: a number is never an IBAN.
    public static string SampleSql(SqlDialect dialect, string from,
        IReadOnlyList<ColumnMeta> columns, int rows)
    {
        var textual = columns.Where(column => Textual(column.DataType)).ToList();
        if (textual.Count == 0) return "";

        var names = string.Join(", ", textual.Select(column => dialect.QuoteIdentifier(column.Name)));

        return dialect.Paginate($"SELECT {names} FROM {from}", 0, Math.Clamp(rows, 1, 5000));
    }

    public static async Task<(IReadOnlyList<ColumnStat> Columns, string? Note)> ReadAsync(
        IDbSession session, SqlDialect dialect, string from, IReadOnlyList<ColumnMeta> columns,
        CancellationToken ct)
    {
        var note = columns.Count > MaxColumns
            ? $"the first {MaxColumns} of {columns.Count} columns"
            : null;

        var counted = columns.Take(MaxColumns).ToList();
        if (counted.Count == 0) return ([], note);

        await using var command = session.Connection.CreateCommand();
        command.CommandText = CountSql(dialect, from, counted);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return ([], note);

        var byName = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, index => index, StringComparer.OrdinalIgnoreCase);

        long Number(string name) =>
            byName.TryGetValue(name, out var index) && !reader.IsDBNull(index)
                ? Convert.ToInt64(reader.GetValue(index))
                : 0;

        string? Text(string name) =>
            byName.TryGetValue(name, out var index) && !reader.IsDBNull(index)
                ? Describe(reader.GetValue(index))
                : null;

        var rows = Number("wds_rows");
        var stats = new List<ColumnStat>();

        for (var i = 0; i < counted.Count; i++)
            stats.Add(new ColumnStat(
                counted[i].Name,
                counted[i].DataType,
                rows,
                Number($"wds_n{i}"),
                byName.ContainsKey($"wds_d{i}") ? Number($"wds_d{i}") : null,
                Text($"wds_lo{i}"),
                Text($"wds_hi{i}")));

        return (stats, note);
    }

    /// What the sampled values look like. Read from the rows, so a column nobody named helpfully is
    /// still found.
    public static async Task<IReadOnlyList<SensitiveHint>> SniffAsync(
        IDbSession session, SqlDialect dialect, string from, IReadOnlyList<ColumnMeta> columns,
        int rows, CancellationToken ct)
    {
        var sql = SampleSql(dialect, from, columns.Take(MaxColumns).ToList(), rows);
        if (sql.Length == 0) return [];

        var seen = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var sampled = 0;

        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            sampled++;

            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (await reader.IsDBNullAsync(i, ct)) continue;

                var value = reader.GetValue(i)?.ToString();
                if (value is null or { Length: 0 }) continue;

                var looks = Looks(value);
                if (looks is null) continue;

                var name = reader.GetName(i);
                if (!seen.TryGetValue(name, out var counts))
                    seen[name] = counts = new Dictionary<string, int>(StringComparer.Ordinal);

                counts[looks] = counts.GetValueOrDefault(looks) + 1;
            }
        }

        // A pattern that matched a few rows out of two hundred is a coincidence; a column that is
        // one is mostly one.
        return
        [
            .. seen
                .SelectMany(column => column.Value
                    .Where(pattern => pattern.Value * 2 >= sampled && pattern.Value > 1)
                    .Select(pattern => new SensitiveHint(column.Key, pattern.Key, pattern.Value, sampled)))
                .OrderByDescending(hint => hint.Percent)
        ];
    }

    /// Rules worth having, read off the numbers. Suggestions, not decisions: each one goes into the
    /// data quality tab as a rule somebody can look at first.
    public static IReadOnlyList<ProfileSuggestion> Suggest(IReadOnlyList<ColumnStat> stats)
    {
        var suggestions = new List<ProfileSuggestion>();

        foreach (var stat in stats)
        {
            if (stat.Rows == 0) continue;

            if (stat.Nulls == 0)
                suggestions.Add(new ProfileSuggestion(stat.Name, QualityKind.NotNull, null,
                    "every row has a value today"));

            if (stat.Unique)
                suggestions.Add(new ProfileSuggestion(stat.Name, QualityKind.Unique, null,
                    "every value is different today"));

            if (Numeric(stat.DataType)
                && decimal.TryParse(stat.Min, NumberStyles.Any, CultureInfo.InvariantCulture, out var low)
                && decimal.TryParse(stat.Max, NumberStyles.Any, CultureInfo.InvariantCulture, out var high))
                suggestions.Add(new ProfileSuggestion(stat.Name, QualityKind.Range,
                    $"{low.ToString(CultureInfo.InvariantCulture)}..{high.ToString(CultureInfo.InvariantCulture)}",
                    $"today's values are between {low} and {high}"));
        }

        return suggestions;
    }

    // --- the shapes worth noticing --------------------------------------------------------------
    // Deliberately few and deliberately strict. A pattern that matches half the world would mark
    // every column and be turned off within a day.

    [GeneratedRegex(@"^[A-Z]{2}\d{2}[A-Z0-9]{10,30}$", RegexOptions.IgnoreCase)]
    private static partial Regex Iban();

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+\.[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    /// 13 to 19 digits, with the separators people write: checked by Luhn below, which is what keeps
    /// an order number from being called a card.
    [GeneratedRegex(@"^\d[\d \-]{11,22}\d$")]
    private static partial Regex CardShape();

    /// A phone number is written, not just long: it starts with a country code or it carries the
    /// separators people type. A bare run of digits is an order number, an account number, an id —
    /// and marking those as phone numbers would mark half of every database.
    [GeneratedRegex(@"^(\+\d[\d\s\-/()]{6,20}\d|\d[\d]*[\s\-/()][\d\s\-/()]{5,20}\d)$")]
    private static partial Regex Phone();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Uuid();

    /// A street and a house number, in the two orders most of Europe and the US write them.
    [GeneratedRegex(@"^(\d{1,5}[a-z]?\s+[\p{L}][\p{L}\s.'-]{2,}|[\p{L}][\p{L}\s.'-]{2,}\s+\d{1,5}[a-z]?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    private static string? Looks(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > 120) return null;

        if (Email().IsMatch(trimmed)) return "an email address";
        if (Uuid().IsMatch(trimmed)) return "a uuid";
        if (Iban().IsMatch(trimmed) && trimmed.Length >= 15) return "an IBAN";
        if (CardShape().IsMatch(trimmed) && Luhn(trimmed)) return "a card number";
        if (Phone().IsMatch(trimmed) && trimmed.Count(char.IsDigit) is >= 9 and <= 15)
            return "a phone number";
        if (Address().IsMatch(trimmed)) return "a street address";

        return null;
    }

    /// The check digit every card number has. Without it, "a card number" would mean "twelve digits".
    private static bool Luhn(string value)
    {
        var digits = value.Where(char.IsDigit).Reverse().Select(c => c - '0').ToList();
        if (digits.Count is < 13 or > 19) return false;

        var sum = 0;

        for (var i = 0; i < digits.Count; i++)
        {
            var digit = digits[i];

            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }

            sum += digit;
        }

        return sum % 10 == 0;
    }

    private static string Describe(object value) => value switch
    {
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        byte[] bytes => $"{bytes.Length} bytes",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool Textual(string type) =>
        Contains(type, "char", "text", "string", "clob", "varchar", "nvarchar", "citext");

    private static bool Numeric(string type) =>
        Contains(type, "int", "dec", "num", "float", "double", "real", "money");

    /// Types no engine will group by, or will only group by slowly enough to matter.
    private static bool Groupable(string type) =>
        !Contains(type, "blob", "bytea", "image", "geometry", "geography", "xml", "clob", "varbinary");

    private static bool Comparable(string type) => Groupable(type) && !Contains(type, "json", "bool");

    private static bool Contains(string type, params string[] words) =>
        words.Any(word => type.Contains(word, StringComparison.OrdinalIgnoreCase));
}
