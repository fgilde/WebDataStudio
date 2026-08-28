using WebDataStudio.Server.Admin;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// One piece of advice, and how much of the captured minute wanted it.
public sealed record CaptureAdvice(
    string Table,
    string Message,
    /// The statement to run, where the advice is one.
    string? Sql,
    /// How many distinct captured statements would use it.
    int Statements,
    /// How many samples those statements were seen in, and the longest one of them.
    int Samples,
    long SlowestMs,
    /// One of the statements, for reading.
    string Example);

/// What the captured minute suggests.
///
/// The capture says what ran; the index advisor says what one statement would like. Neither answers
/// the question somebody has after watching a server for a minute — "so what should I change?" —
/// which is the two of them together: the same advice asked for by several statements, ordered by how
/// much of the minute it would help.
public static class CaptureAdvisor
{
    /// Enough statements to cover what a minute saw, few enough that the introspection behind it
    /// stays a handful of queries.
    private const int MaxStatements = 20;

    public static async Task<IReadOnlyList<CaptureAdvice>> SuggestAsync(
        IDbDriver driver, IDbSession session, IReadOnlyList<CapturedStatement> captured,
        CancellationToken ct)
    {
        // The slowest first: with a cap, those are the ones worth the introspection.
        var statements = captured
            .OrderByDescending(statement => statement.MaxDurationMs)
            .Take(MaxStatements)
            .ToList();

        var byAdvice = new Dictionary<(string Table, string Message), CaptureAdvice>();

        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();

            var tables = await TableLoader.LoadAsync(driver, session, statement.Text, ct);
            if (tables.Count == 0) continue;

            // No plan: a captured statement carries the parameters of somebody else's session, and
            // EXPLAIN would refuse it. The advice from the SQL alone is the honest half.
            foreach (var finding in IndexAdvisor.Suggest(statement.Text, null, tables, driver.Dialect))
            {
                // The title carries the table ("Index suggestion for orders") and the statement is
                // the CREATE INDEX; the same advice from two statements is one row.
                var key = (Table(finding.Title), finding.Title);

                byAdvice[key] = byAdvice.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        Statements = existing.Statements + 1,
                        Samples = existing.Samples + statement.Samples,
                        SlowestMs = Math.Max(existing.SlowestMs, statement.MaxDurationMs),
                        Example = statement.MaxDurationMs > existing.SlowestMs
                            ? statement.Text
                            : existing.Example,
                    }
                    : new CaptureAdvice(key.Item1, finding.Detail, finding.Statement, 1,
                        statement.Samples, statement.MaxDurationMs, statement.Text);
            }
        }

        return byAdvice.Values
            // What would help the most of the minute, and among equals the slowest statement.
            .OrderByDescending(advice => advice.Statements)
            .ThenByDescending(advice => advice.SlowestMs)
            .ToList();
    }

    /// "Index suggestion for orders" — the last word is the table.
    private static string Table(string title)
    {
        var at = title.LastIndexOf(' ');
        return at < 0 ? title : title[(at + 1)..];
    }
}
