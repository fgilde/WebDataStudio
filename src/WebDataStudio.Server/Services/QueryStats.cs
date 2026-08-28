using System.Globalization;
using System.Text.RegularExpressions;

namespace WebDataStudio.Server.Services;

/// One statement, however many times it ran.
public sealed record StatementStats(
    /// The statement with its literals replaced, which is what makes two runs of "the same query"
    /// the same row here.
    string Fingerprint,
    /// One of the actual statements, for reading and for opening in a query tab.
    string Example,
    int Runs,
    int Failures,
    long AverageMs,
    long SlowestMs,
    long FastestMs,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    /// The average of the recent half against the average of the older half, as a factor: 2 means it
    /// takes twice as long as it used to. Null where there is not enough history to say.
    double? Trend);

/// Which statements run here, how often, and whether they are getting slower.
///
/// The history already holds every run with its elapsed time; nobody ever reads it as a whole,
/// because a list of two thousand statements answers no question. Grouped by fingerprint it answers
/// two: what does this connection actually spend its time on, and what changed since last week.
public static class QueryStats
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline;

    private static readonly Regex Comments = new(@"--[^\n]*|/\*.*?\*/", Options);
    private static readonly Regex Strings = new(@"'(?:[^']|'')*'", Options);
    private static readonly Regex Numbers = new(@"(?<![A-Za-z_\.\d])\d+(\.\d+)?", Options);
    private static readonly Regex Parameters = new(@"[@$:]\w+", Options);
    private static readonly Regex InList = new(@"\bIN\s*\(\s*\?(\s*,\s*\?)*\s*\)", Options);
    private static readonly Regex Whitespace = new(@"\s+", Options);

    /// Two runs of the same query with different values are the same statement. Literals, bind
    /// parameters and the length of an `IN` list are exactly the differences that do not matter.
    public static string Fingerprint(string sql)
    {
        var text = Comments.Replace(sql, " ");
        text = Strings.Replace(text, "?");
        text = Parameters.Replace(text, "?");
        text = Numbers.Replace(text, "?");
        text = InList.Replace(text, "IN (?)");
        text = Whitespace.Replace(text, " ").Trim().TrimEnd(';');

        return text;
    }

    /// The report. Takes the entries rather than the store, so every case that matters — a statement
    /// that got slower, one that only ever failed, one run once — is a test without a database.
    public static IReadOnlyList<StatementStats> Report(
        IEnumerable<HistoryEntry> entries, int top = 50)
    {
        var groups = entries
            .Where(entry => entry.Sql.Trim().Length > 0)
            .GroupBy(entry => Fingerprint(entry.Sql));

        var stats = new List<StatementStats>();

        foreach (var group in groups)
        {
            var runs = group.OrderBy(entry => entry.ExecutedAt).ToList();
            var timed = runs.Where(entry => entry.ElapsedMs is > 0).ToList();

            stats.Add(new StatementStats(
                group.Key,
                runs[^1].Sql.Trim(),
                runs.Count,
                runs.Count(entry => entry.Error is { Length: > 0 }),
                timed.Count == 0 ? 0 : (long)timed.Average(entry => entry.ElapsedMs!.Value),
                timed.Count == 0 ? 0 : timed.Max(entry => entry.ElapsedMs!.Value),
                timed.Count == 0 ? 0 : timed.Min(entry => entry.ElapsedMs!.Value),
                runs[0].ExecutedAt,
                runs[^1].ExecutedAt,
                Trend(timed)));
        }

        // The slowest on average first: that is the question this list is opened to answer.
        return stats
            .OrderByDescending(entry => entry.AverageMs)
            .ThenByDescending(entry => entry.Runs)
            .Take(Math.Clamp(top, 1, 500))
            .ToList();
    }

    /// Recent against older, as a factor. Four runs at least: with two, "twice as slow" is noise.
    private static double? Trend(IReadOnlyList<HistoryEntry> timed)
    {
        if (timed.Count < 4) return null;

        var half = timed.Count / 2;
        var older = timed.Take(half).Average(entry => entry.ElapsedMs!.Value);
        var recent = timed.Skip(half).Average(entry => entry.ElapsedMs!.Value);

        if (older <= 0) return null;

        return Math.Round(recent / older, 2, MidpointRounding.AwayFromZero);
    }

    /// "1.8× slower since it started being recorded" — the sentence a panel puts next to the arrow.
    public static string Describe(double? trend) => trend switch
    {
        null => "not enough history",
        >= 1.25 => string.Create(CultureInfo.InvariantCulture, $"{trend:0.0}× slower than it was"),
        <= 0.8 => string.Create(CultureInfo.InvariantCulture, $"{1 / trend!.Value:0.0}× faster than it was"),
        _ => "about the same",
    };
}
