using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// A saved query somebody can run without knowing any SQL: its name, the connection it belongs to,
/// and the parameters it asks for.
public sealed record Report(
    string Id, string Name, string? Folder, string ConnectionId, IReadOnlyList<string> Parameters,
    string Sql);

/// A saved query, offered as a form.
///
/// Saved queries, bind parameters and shared results already exist separately. What was missing is
/// the shape a person who does not write SQL can use: pick the report, fill in the boxes, press run.
/// The link carries the values, so "the numbers for last month" is something to send rather than to
/// explain.
///
/// Reading only. A report is something somebody who has never seen this database is going to press,
/// and that is not the place to find out that a saved query had a `DELETE` in it.
public static class Reports
{
    /// The marker somebody types for a bind parameter, per engine.
    ///
    /// Not `SqlDialect.ParameterPrefix`: that is what the ADO.NET provider needs on the wire, and for
    /// PostgreSQL it says `@` while the editor offers `:` — Npgsql accepts both. A report has to find
    /// the parameters the *editor* found, or a saved query with boxes in it would arrive here with no
    /// boxes at all. So this is the editor's own table, on the server side.
    public static char MarkerFor(string engine) => engine switch
    {
        "postgresql" or "oracle" => ':',
        "sqlite" or "duckdb" => '$',
        "sqlserver" or "mysql" => '@',
        // ClickHouse writes {name:Type}, which is not a marker followed by a name, and Redis and
        // MongoDB have no statements to bind into.
        _ => '\0',
    };

    /// The parameters a statement asks for, in the order it asks. Comments and string literals are
    /// skipped, so `-- :note` is a comment and ':' inside a literal is a colon.
    public static IReadOnlyList<string> Parameters(string sql, string engine)
    {
        var marker = MarkerFor(engine);
        if (marker == '\0') return [];
        var found = new List<string>();
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
                continue;
            }

            if (c is '\'' or '"' or '`' or '[')
            {
                var closer = c == '[' ? ']' : c;
                i++;

                while (i < sql.Length && sql[i] != closer) i++;
                i++;
                continue;
            }

            if (c == marker && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                // A PostgreSQL cast — `value::text` — is two colons and not a parameter.
                if (marker == ':' && i > 0 && sql[i - 1] == ':')
                {
                    i++;
                    continue;
                }

                var start = ++i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;

                var name = sql[start..i];
                if (!found.Contains(name, StringComparer.Ordinal)) found.Add(name);
                continue;
            }

            i++;
        }

        return found;
    }

    /// Every saved query that names a connection, as a report. A saved query with no connection has
    /// nothing to run against, so it is not offered as one.
    public static IReadOnlyList<Report> All(WorkspaceStore workspace, ConnectionRegistry connections)
    {
        if (!workspace.Available) return [];

        var reports = new List<Report>();

        foreach (var saved in workspace.ListSavedQueries())
        {
            if (saved.ConnectionId is not { Length: > 0 } connectionId) continue;

            var spec = connections.Find(connectionId);
            if (spec is null) continue;

            reports.Add(new Report(saved.Id, saved.Name, saved.Folder, connectionId,
                Parameters(saved.Sql, spec.Engine), saved.Sql));
        }

        return reports;
    }
}
