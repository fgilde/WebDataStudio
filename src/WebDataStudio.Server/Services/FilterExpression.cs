using System.Globalization;
using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// What a column filter turned into: a SQL fragment for the WHERE clause, and the values it needs.
/// The fragment never contains a value — everything typed travels as a parameter.
public sealed record FilterCondition(string Sql, IReadOnlyDictionary<string, object?> Parameters)
{
    public static readonly FilterCondition None = new("", new Dictionary<string, object?>());

    public bool IsEmpty => Sql.Length == 0;
}

/// How a column is compared. Read off the declared type, because "> 5" means one thing on a number
/// and another on text, and "TODAY" means nothing at all on either.
public enum FilterKind { Text, Number, Date, Boolean }

/// A small filter language for a column box, borrowed from what DbGate got right: one line of
/// typing instead of a dialog.
///
///   ^ada        starts with        !^ada   does not start with
///   $son        ends with          !$son   does not end with
///   +ad         contains           ~ad     does not contain
///   =ada        equals             !=ada   does not
///   &gt;10, &lt;=20    compared as a number (or a date)
///   NULL, NOT NULL, EMPTY, NOT EMPTY
///   TODAY, YESTERDAY, THIS WEEK, LAST MONTH, NEXT YEAR, 2026, 2026-08, 2026-08-23
///   "two words"  a value with spaces in it
///
/// Whitespace is AND, a comma is OR, and OR binds looser: `&gt;10 &lt;20, =0` is
/// `(&gt; 10 AND &lt; 20) OR = 0`. A bare word is "contains" on text and "equals" on everything else,
/// which is what people type when they do not know there is a language here at all.
public static class FilterExpression
{
    /// Everything after this many terms is ignored: a filter box is not a query language, and a
    /// pasted paragraph should not become a thousand OR branches.
    private const int MaxTerms = 32;

    public static FilterKind KindOf(string dataType)
    {
        var type = dataType.ToLowerInvariant();

        if (type.Contains("bool") || type.Contains("bit")) return FilterKind.Boolean;

        // "date", "datetime", "timestamp", "timestamptz", "time with time zone" — all of them
        // carry one of these two words, and nothing else does.
        if (type.Contains("date") || type.Contains("time")) return FilterKind.Date;

        if (type.Contains("int") || type.Contains("dec") || type.Contains("num")
            || type.Contains("real") || type.Contains("double") || type.Contains("float")
            || type.Contains("money") || type.Contains("serial"))
            return FilterKind.Number;

        return FilterKind.Text;
    }

    /// Builds the condition for one column. `prefix` names the parameters, so two columns filtered
    /// at once cannot collide.
    /// `now` is what the named periods are measured from; it is a parameter so a test can ask what
    /// "LAST WEEK" meant on a particular day.
    public static FilterCondition Build(SqlDialect dialect, string columnSql, FilterKind kind,
        string filter, string prefix, DateTime? now = null)
    {
        var groups = new List<string>();
        var parameters = new Dictionary<string, object?>();
        var next = 0;

        foreach (var group in SplitOr(filter))
        {
            var terms = new List<string>();

            foreach (var term in SplitAnd(group))
            {
                if (next >= MaxTerms) break;

                var sql = Term(dialect, columnSql, kind, term, $"{prefix}{next}", parameters, now);
                if (sql is not null) { terms.Add(sql); next++; }
            }

            if (terms.Count > 0) groups.Add(terms.Count == 1 ? terms[0] : $"({string.Join(" AND ", terms)})");
        }

        if (groups.Count == 0) return FilterCondition.None;

        return new FilterCondition(
            groups.Count == 1 ? groups[0] : $"({string.Join(" OR ", groups)})", parameters);
    }

    /// A script request carries strings, so the values have to be spelled in a way every engine
    /// reads the same. Never the current culture: "23.08.2026" is a date on one machine and a
    /// syntax error on the next.
    public static Dictionary<string, string?> AsText(IReadOnlyDictionary<string, object?> parameters) =>
        parameters.ToDictionary(entry => entry.Key, entry => entry.Value switch
        {
            null => null,
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            // Not "true"/"false": a bit column takes 1 and 0 on SQL Server, and every engine takes
            // those.
            bool flag => flag ? "1" : "0",
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            var other => Convert.ToString(other, CultureInfo.InvariantCulture),
        });

    /// One term, or null when it says nothing. Adds whatever it needs to `parameters`.
    private static string? Term(SqlDialect dialect, string column, FilterKind kind, string term,
        string name, Dictionary<string, object?> parameters, DateTime? now)
    {
        term = term.Trim();
        if (term.Length == 0) return null;

        var p = dialect.ParameterPrefix + name;
        var text = $"CAST({column} AS {dialect.TextType})";

        // A parameter arrives as text, and PostgreSQL will not compare a number to text. What the
        // value has to be read as depends on the column, so the dialect says how.
        var typed = kind switch
        {
            FilterKind.Number => string.Format(CultureInfo.InvariantCulture, dialect.NumberCast, p),
            FilterKind.Date => string.Format(CultureInfo.InvariantCulture, dialect.TimestampCast, p),
            _ => p,
        };

        // Text is compared without case, on every engine. PostgreSQL's LIKE is case-sensitive and
        // MySQL's is not, so "ada" used to find Adam on one connection and nothing on the next.
        var lower = $"LOWER({text})";

        // The words first: they are the same on every kind of column, and "NULL" as a search string
        // is what quotes are for.
        switch (term.ToUpperInvariant())
        {
            case "NULL": return $"{column} IS NULL";
            case "NOT NULL": return $"{column} IS NOT NULL";
            case "EMPTY": return $"({column} IS NULL OR {text} = '')";
            case "NOT EMPTY": return $"({column} IS NOT NULL AND {text} <> '')";
            case "TRUE": return kind == FilterKind.Boolean
                ? Value(column, "=", name, p, parameters, true) : null;
            case "FALSE": return kind == FilterKind.Boolean
                ? Value(column, "=", name, p, parameters, false) : null;
        }

        if (kind == FilterKind.Date && Period(term, now) is { } period)
        {
            parameters[name] = period.From;
            parameters[name + "b"] = period.To;

            var upper = string.Format(CultureInfo.InvariantCulture, dialect.TimestampCast,
                dialect.ParameterPrefix + name + "b");

            // Half-open, so a timestamp at 23:59 on the last day is still in "this month".
            return $"({column} >= {typed} AND {column} < {upper})";
        }

        // An operator, longest spelling first: "!=" must not be read as "!" then "=".
        foreach (var (token, op) in Operators)
            if (term.StartsWith(token, StringComparison.Ordinal))
            {
                var rest = Unquote(term[token.Length..].Trim());
                if (rest.Length == 0) return null;

                return op switch
                {
                    "^" => Like(lower, name, p, parameters, rest + "%", false),
                    "!^" => Like(lower, name, p, parameters, rest + "%", true),
                    "$" => Like(lower, name, p, parameters, "%" + rest, false),
                    "!$" => Like(lower, name, p, parameters, "%" + rest, true),
                    "+" => Like(lower, name, p, parameters, "%" + rest + "%", false),
                    "~" => Like(lower, name, p, parameters, "%" + rest + "%", true),
                    _ => Typed(dialect, column, lower, typed, kind, op, rest, name, parameters),
                };
            }

        var bare = Unquote(term);

        // Nothing said how to compare, so: text contains, everything else equals. On a number that
        // is not a number at all, there is nothing to ask for.
        return kind == FilterKind.Text
            ? Like(lower, name, p, parameters, "%" + bare + "%", false)
            : Typed(dialect, column, lower, typed, kind, "=", bare, name, parameters);
    }

    /// The operators, ordered so a longer one is tried before its own prefix.
    private static readonly (string Token, string Op)[] Operators =
    [
        ("!^", "!^"), ("!$", "!$"), ("!=", "!="), ("<>", "!="), ("<=", "<="), (">=", ">="),
        ("^", "^"), ("$", "$"), ("+", "+"), ("~", "~"), ("=", "="), ("<", "<"), (">", ">"),
    ];

    private static string Like(string text, string name, string p,
        Dictionary<string, object?> parameters, string pattern, bool negated)
    {
        parameters[name] = pattern.ToLowerInvariant();
        // A NULL never matches a LIKE, and "does not contain x" has to hold for a row with no value
        // at all — otherwise the two halves of a filter do not add up to everything.
        return negated ? $"({text} NOT LIKE {p} OR {text} IS NULL)" : $"{text} LIKE {p}";
    }

    private static string Value(string column, string op, string name, string p,
        Dictionary<string, object?> parameters, object? value)
    {
        parameters[name] = value;
        return $"{column} {op} {p}";
    }

    /// A comparison against a number, a date or a boolean. What cannot be read as one is compared
    /// as text instead of being thrown away: `=abc` on a numeric column is a question, not a typo.
    private static string? Typed(SqlDialect dialect, string column, string text, string typed,
        FilterKind kind, string op, string value, string name, Dictionary<string, object?> parameters)
    {
        var p = dialect.ParameterPrefix + name;

        switch (kind)
        {
            case FilterKind.Number
                when decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number):
                return Value(column, op, name, typed, parameters, number);

            case FilterKind.Date when Day(value) is { } day:
                // A day is a range: "= 2026-08-23" has to catch every time on that day.
                if (op == "=" || op == "!=")
                {
                    parameters[name] = day;
                    parameters[name + "b"] = day.AddDays(1);

                    var upper = string.Format(CultureInfo.InvariantCulture, dialect.TimestampCast,
                        dialect.ParameterPrefix + name + "b");

                    var inside = $"({column} >= {typed} AND {column} < {upper})";
                    return op == "=" ? inside : $"NOT {inside}";
                }

                return Value(column, op, name, typed, parameters, day);

            case FilterKind.Boolean when bool.TryParse(value, out var flag):
                return Value(column, op, name, p, parameters, flag);

            case FilterKind.Boolean when value is "1" or "0":
                return Value(column, op, name, p, parameters, value == "1");

            default:
                // Comparing as text keeps ">=2026" useful on a column the engine calls a string.
                // `text` arrives lowered, so the value is lowered with it.
                return Value(text, op, name, p, parameters, value.ToLowerInvariant());
        }
    }

    private static DateTime? Day(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    /// The named periods, and the shorthands that are a period rather than a day: `2026` is a year
    /// and `2026-08` is a month.
    public static (DateTime From, DateTime To)? Period(string term, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var word = term.ToUpperInvariant().Replace("  ", " ");

        // Monday, because a week that starts on Sunday surprises everybody who is not American.
        var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var first = new DateTime(today.Year, today.Month, 1);
        var january = new DateTime(today.Year, 1, 1);

        return word switch
        {
            "TODAY" => (today, today.AddDays(1)),
            "YESTERDAY" => (today.AddDays(-1), today),
            "TOMORROW" => (today.AddDays(1), today.AddDays(2)),
            "THIS WEEK" => (monday, monday.AddDays(7)),
            "LAST WEEK" => (monday.AddDays(-7), monday),
            "NEXT WEEK" => (monday.AddDays(7), monday.AddDays(14)),
            "THIS MONTH" => (first, first.AddMonths(1)),
            "LAST MONTH" => (first.AddMonths(-1), first),
            "NEXT MONTH" => (first.AddMonths(1), first.AddMonths(2)),
            "THIS YEAR" => (january, january.AddYears(1)),
            "LAST YEAR" => (january.AddYears(-1), january),
            "NEXT YEAR" => (january.AddYears(1), january.AddYears(2)),
            _ => Shorthand(word),
        };
    }

    private static (DateTime, DateTime)? Shorthand(string word)
    {
        if (word.Length == 4 && int.TryParse(word, out var year) && year is > 1000 and < 9999)
            return (new DateTime(year, 1, 1), new DateTime(year + 1, 1, 1));

        if (word.Length == 7 && word[4] == '-'
            && int.TryParse(word[..4], out var y) && int.TryParse(word[5..], out var month)
            && month is >= 1 and <= 12)
        {
            var from = new DateTime(y, month, 1);
            return (from, from.AddMonths(1));
        }

        return null;
    }

    /// A value in double quotes keeps its spaces, its commas and its leading operator.
    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\"\"", "\"")
            : value;

    internal static IEnumerable<string> SplitOr(string filter) => Split(filter, ',');

    /// The two-word terms. Whitespace is AND, so without this "NOT NULL" would be read as "contains
    /// not" AND "is null" — and "THIS WEEK" as two words nobody wrote.
    private static readonly HashSet<string> TwoWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT NULL", "NOT EMPTY",
        "THIS WEEK", "LAST WEEK", "NEXT WEEK",
        "THIS MONTH", "LAST MONTH", "NEXT MONTH",
        "THIS YEAR", "LAST YEAR", "NEXT YEAR",
    };

    internal static IEnumerable<string> SplitAnd(string group)
    {
        var words = Split(group, ' ').ToList();

        for (var index = 0; index < words.Count; index++)
        {
            // Only a pair that is actually one of the terms is joined: "not important" on a text
            // column stays two words, which is what somebody typing it means.
            if (index + 1 < words.Count && TwoWords.Contains($"{words[index]} {words[index + 1]}"))
            {
                yield return $"{words[index]} {words[index + 1]}";
                index++;
                continue;
            }

            yield return words[index];
        }
    }

    /// Splits on a separator that is not inside double quotes. Everything else about the language
    /// is decided per term, so this is the only place quoting has to be understood.
    private static IEnumerable<string> Split(string text, char separator)
    {
        var current = new StringBuilder();
        var quoted = false;

        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; current.Append(c); continue; }

            if (c == separator && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) yield return current.ToString();
    }
}
