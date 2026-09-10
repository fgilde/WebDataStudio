using System.Globalization;
using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// A dashboard's time range, resolved to two instants.
public sealed record ResolvedRange(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Span => To - From;
}

/// What a widget's statement is allowed to read from the dashboard around it: the range, the chosen
/// variable values, and how wide the widget is on screen (which is what `$__interval` is for).
public sealed record DashboardContext(
    string? From = null,
    string? To = null,
    Dictionary<string, string[]>? Variables = null,
    int? WidthPx = null);

/// What came out of an expansion: the statement to run, and the values to bind.
public sealed record ExpandedSql(string Sql, Dictionary<string, string?> Parameters);

/// A value a dashboard may not put into a statement.
public sealed class DashboardValueException(string message) : Exception(message);

/// Relative times, the way every dashboard writes them: `now`, `now-24h`, `now-7d/d`.
public static class DashboardTime
{
    private static readonly Regex Relative = new(
        @"^now(?<offset>[-+]\d+(?<unit>[smhdwMy]))?(?<snap>/[smhdwMy])?$",
        RegexOptions.Compiled);

    public static ResolvedRange Resolve(string? from, string? to, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.UtcNow;
        var start = Instant(from, reference) ?? reference.AddDays(-1);
        var end = Instant(to, reference) ?? reference;

        // A range that runs backwards is a picker somebody dragged the wrong way, not an error
        // worth refusing a dashboard over.
        return start <= end ? new ResolvedRange(start, end) : new ResolvedRange(end, start);
    }

    public static DateTimeOffset? Instant(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();

        if (Relative.Match(text) is { Success: true } match)
        {
            var instant = now;

            if (match.Groups["offset"].Success)
            {
                var offset = match.Groups["offset"].Value;
                var amount = int.Parse(offset[..^1], CultureInfo.InvariantCulture);
                instant = Add(instant, amount, offset[^1]);
            }

            if (match.Groups["snap"].Success) instant = Snap(instant, match.Groups["snap"].Value[1]);

            return instant;
        }

        // Epoch milliseconds, which is what a Grafana dashboard's absolute range looks like.
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            return DateTimeOffset.FromUnixTimeMilliseconds(epoch);

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset Add(DateTimeOffset instant, int amount, char unit) => unit switch
    {
        's' => instant.AddSeconds(amount),
        'm' => instant.AddMinutes(amount),
        'h' => instant.AddHours(amount),
        'd' => instant.AddDays(amount),
        'w' => instant.AddDays(amount * 7),
        'M' => instant.AddMonths(amount),
        'y' => instant.AddYears(amount),
        _ => instant,
    };

    /// `now/d` is "the start of today", which is what somebody means by a day rather than
    /// "twenty-four hours ago".
    private static DateTimeOffset Snap(DateTimeOffset instant, char unit) => unit switch
    {
        's' => new DateTimeOffset(instant.Year, instant.Month, instant.Day, instant.Hour, instant.Minute, instant.Second, instant.Offset),
        'm' => new DateTimeOffset(instant.Year, instant.Month, instant.Day, instant.Hour, instant.Minute, 0, instant.Offset),
        'h' => new DateTimeOffset(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, instant.Offset),
        'd' => new DateTimeOffset(instant.Year, instant.Month, instant.Day, 0, 0, 0, instant.Offset),
        'w' => new DateTimeOffset(instant.Year, instant.Month, instant.Day, 0, 0, 0, instant.Offset)
            .AddDays(-(int)instant.DayOfWeek),
        'M' => new DateTimeOffset(instant.Year, instant.Month, 1, 0, 0, 0, instant.Offset),
        'y' => new DateTimeOffset(instant.Year, 1, 1, 0, 0, 0, instant.Offset),
        _ => instant,
    };
}

/// The one place a dashboard's time range and its variables become SQL.
///
/// Two rules make this a boundary rather than a template engine. A single value **binds as a
/// parameter** — it never appears in the statement text, so it cannot be anything but a value. A
/// list has to be inlined, because no driver binds an `IN` list, and each entry is quoted by the
/// dialect and refused if it carries what a quoted literal cannot hold. A dashboard is a document
/// people paste to each other; it does not get to be a way to run somebody else's SQL.
public static partial class DashboardSql
{
    /// The parameter names this adds. Prefixed so they cannot collide with a statement's own.
    private const string Prefix = "wds_dash_";

    [GeneratedRegex(@"\$__timeFilter\(\s*(?<column>[^)]*?)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex TimeFilter();

    [GeneratedRegex(@"\$__(?<name>from|to|fromIso|toIso|interval|intervalMs)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Macro();

    /// `${name:csv}`, `${name}` and `$name`, in that order — the long forms first, so `${a}` is not
    /// read as `$a` followed by a brace.
    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(:(?<format>[a-z]+))?\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Variable();

    public static ExpandedSql Expand(string sql, SqlDialect dialect, DashboardContext? context,
        DateTimeOffset? now = null)
    {
        var parameters = new Dictionary<string, string?>();
        if (string.IsNullOrWhiteSpace(sql)) return new ExpandedSql(sql ?? "", parameters);
        if (context is null) return new ExpandedSql(sql, parameters);

        var range = DashboardTime.Resolve(context.From, context.To, now);
        var expanded = Variables(sql, dialect, context, parameters);

        expanded = TimeFilter().Replace(expanded, match =>
        {
            var column = match.Groups["column"].Value.Trim();
            // A filter with no column is a widget somebody has not finished; leaving it true keeps
            // the rest of the statement runnable and shows rows rather than an error.
            if (column.Length == 0) return "1 = 1";

            string from, to;

            if (dialect.ParameterPrefix.Length == 0)
            {
                from = dialect.QuoteLiteral(range.From.ToString("O"));
                to = dialect.QuoteLiteral(range.To.ToString("O"));
            }
            else
            {
                Bind(parameters, "from", range.From.ToString("O"));
                Bind(parameters, "to", range.To.ToString("O"));

                from = string.Format(CultureInfo.InvariantCulture, dialect.TimestampCast, Name(dialect, "from"));
                to = string.Format(CultureInfo.InvariantCulture, dialect.TimestampCast, Name(dialect, "to"));
            }

            return $"{column} >= {from} AND {column} < {to}";
        });

        var interval = Interval(range, context.WidthPx);

        return new ExpandedSql(Macro().Replace(expanded, match => match.Groups["name"].Value.ToLowerInvariant() switch
        {
            "from" => range.From.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "to" => range.To.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            "fromiso" => dialect.QuoteLiteral(range.From.ToString("O")),
            "toiso" => dialect.QuoteLiteral(range.To.ToString("O")),
            "intervalms" => ((long)interval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            "interval" => dialect.QuoteLiteral(Words(interval)),
            _ => match.Value,
        }), parameters);
    }

    /// A bucket width from the range and how wide the widget is: one bucket per two pixels, rounded
    /// to something a person reads on an axis.
    public static TimeSpan Interval(ResolvedRange range, int? widthPx)
    {
        var buckets = Math.Clamp((widthPx ?? 800) / 2, 20, 2000);
        var raw = range.Span / buckets;

        TimeSpan[] steps =
        [
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
            TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
            TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30),
        ];

        return steps.FirstOrDefault(step => step >= raw, TimeSpan.FromDays(365));
    }

    /// The interval as an engine reads it in an interval literal: `15 minute`, `1 day`.
    private static string Words(TimeSpan interval) => interval switch
    {
        { TotalDays: >= 1 } => $"{(int)interval.TotalDays} day",
        { TotalHours: >= 1 } => $"{(int)interval.TotalHours} hour",
        { TotalMinutes: >= 1 } => $"{(int)interval.TotalMinutes} minute",
        _ => $"{Math.Max(1, (int)interval.TotalSeconds)} second",
    };

    private static string Variables(string sql, SqlDialect dialect, DashboardContext context,
        Dictionary<string, string?> parameters) =>
        Variable().Replace(sql, match =>
        {
            var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["bare"].Value;

            // `$__from` and friends are handled above; a variable nobody defined stays as it is,
            // because `$1` and `$name` are also what a driver's own placeholders look like.
            if (name.StartsWith("__", StringComparison.Ordinal)) return match.Value;
            if (context.Variables is null || !context.Variables.TryGetValue(name, out var values))
                return match.Value;

            var format = match.Groups["format"].Success
                ? match.Groups["format"].Value.ToLowerInvariant()
                : null;

            var chosen = (values ?? []).Where(value => value is not null).ToList();

            foreach (var value in chosen) Check(name, value);

            // A list cannot be bound: no driver binds an `IN` list. So it is quoted per dialect,
            // which is the one place in the studio where a dashboard's value reaches the statement
            // text — and the only reason the check above exists.
            if (format == "csv" || chosen.Count > 1)
                return chosen.Count == 0
                    ? "NULL"
                    : string.Join(", ", chosen.Select(dialect.QuoteLiteral));

            if (format == "raw")
                throw new DashboardValueException(
                    $"the variable '{name}' asks to be inserted raw, and this studio does not do "
                    + "that: a dashboard is a document people paste to each other");

            var single = chosen.Count == 1 ? chosen[0] : "";

            // An engine with no parameters at all — a resource path rather than SQL — has nothing to
            // bind to, so the value is quoted the way that engine quotes one.
            if (dialect.ParameterPrefix.Length == 0) return dialect.QuoteLiteral(single);

            Bind(parameters, $"var_{name}", single);

            return Name(dialect, $"var_{name}");
        });

    /// What a value may not carry. Everything here is something that only matters once a value is
    /// inlined — and it is checked for every value, list or not, so a single-value variable that a
    /// widget later reads as a list cannot slip through.
    private static void Check(string name, string value)
    {
        if (value.Length > 500)
            throw new DashboardValueException(
                $"the value for '{name}' is longer than five hundred characters, which is not a "
                + "value a dashboard filters on");

        if (value.Any(c => c is '\0' or '\n' or '\r'))
            throw new DashboardValueException(
                $"the value for '{name}' carries a line break, and a dashboard's values are "
                + "single-line: nothing legitimate needs one, and everything that does is an "
                + "attempt at a second statement");
    }

    private static void Bind(Dictionary<string, string?> parameters, string name, string value) =>
        parameters[Prefix + name] = value;

    private static string Name(SqlDialect dialect, string name) => dialect.ParameterPrefix + Prefix + name;
}
