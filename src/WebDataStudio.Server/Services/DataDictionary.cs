using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// The document somebody asks for when they join the team: what is in this database, what each
/// table is for, and how they hang together.
///
/// The studio already knows all of it — columns, keys, comments, row counts, and the notes people
/// left on objects here. What was missing was one file that says it in order, in a form you can
/// send to somebody who does not have the studio open.
public static class DataDictionary
{
    /// How many tables are described in full before the document stops and says so. Describing a
    /// table costs several round trips, and a schema with two thousand of them is a different
    /// request than the one somebody meant to make.
    public const int DefaultLimit = 200;

    public static async Task<string> WriteAsync(IDbDriver driver, IDbSession session,
        IReadOnlyList<SchemaNodeRef> tables, Func<string, IReadOnlyList<string>> notesFor,
        string title, int limit, CancellationToken ct)
    {
        var markdown = new StringBuilder();

        markdown.AppendLine($"# {title}");
        markdown.AppendLine();
        markdown.AppendLine($"{driver.Info.Label} · {tables.Count} table(s) · "
            + $"written {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        markdown.AppendLine();

        var described = new List<(SchemaNodeRef Ref, ObjectDetail Detail)>();

        foreach (var table in tables.Take(limit))
        {
            ct.ThrowIfCancellationRequested();

            // One unreadable table is not a reason to have no document: it is named and skipped.
            try
            {
                described.Add((table, await driver.DescribeAsync(session, table, ct)));
            }
            catch (Exception e)
            {
                markdown.AppendLine($"> `{table.Name}` could not be read: {e.Message}");
                markdown.AppendLine();
            }
        }

        // --- the overview, which is the half most people actually read -------------------------
        markdown.AppendLine("## Tables");
        markdown.AppendLine();
        markdown.AppendLine("| Table | Rows | Size | What it is |");
        markdown.AppendLine("|---|---|---|---|");

        foreach (var (table, detail) in described)
            markdown.AppendLine($"| [{table.Name}](#{Anchor(table.Name)}) | {Count(detail.RowCount)} "
                + $"| {Size(detail.SizeBytes)} | {Cell(detail.Comment)} |");

        markdown.AppendLine();

        if (tables.Count > limit)
        {
            markdown.AppendLine($"> {tables.Count - limit} more table(s) are not described here: "
                + $"this document stops at {limit}.");
            markdown.AppendLine();
        }

        // --- and then each one in full -----------------------------------------------------------
        foreach (var (table, detail) in described)
        {
            markdown.AppendLine($"## {table.Name}");
            markdown.AppendLine();

            if (detail.Comment is { Length: > 0 })
            {
                markdown.AppendLine(detail.Comment);
                markdown.AppendLine();
            }

            markdown.AppendLine("| Column | Type | Null | Default | Key | What it is |");
            markdown.AppendLine("|---|---|---|---|---|---|");

            foreach (var column in detail.Columns)
                markdown.AppendLine($"| {Cell(column.Name)} | `{column.DataType}` "
                    + $"| {(column.Nullable ? "yes" : "no")} | {Code(column.Default)} "
                    + $"| {(column.IsPrimaryKey ? "PK" : "")} | {Cell(column.Comment)} |");

            markdown.AppendLine();

            if (detail.ForeignKeys.Count > 0)
            {
                markdown.AppendLine("**Points at**");
                markdown.AppendLine();

                foreach (var key in detail.ForeignKeys)
                    markdown.AppendLine($"- `{string.Join(", ", key.Columns)}` → "
                        + $"`{key.ReferencedTable}({string.Join(", ", key.ReferencedColumns)})`");

                markdown.AppendLine();
            }

            if (detail.Indexes.Count > 0)
            {
                markdown.AppendLine("**Indexes**");
                markdown.AppendLine();

                foreach (var index in detail.Indexes)
                    markdown.AppendLine($"- `{index.Name}` on `{string.Join(", ", index.Columns)}`"
                        + (index.Unique ? " — unique" : ""));

                markdown.AppendLine();
            }

            // What people wrote about this object in the studio. A data dictionary without the
            // sentence somebody left on the table last year is missing the part that was not
            // derivable from the schema in the first place.
            var notes = notesFor(table.ToString());

            if (notes.Count > 0)
            {
                markdown.AppendLine("**Notes**");
                markdown.AppendLine();
                foreach (var note in notes) markdown.AppendLine($"- {note}");
                markdown.AppendLine();
            }
        }

        return markdown.ToString();
    }

    /// GitHub's heading anchors, closely enough for a table name: lower case, spaces to hyphens,
    /// nothing else kept.
    private static string Anchor(string name) =>
        new(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private static string Cell(string? text) =>
        text is null or { Length: 0 } ? "" : text.Replace("|", "\\|").ReplaceLineEndings(" ");

    private static string Code(string? text) =>
        text is null or { Length: 0 } ? "" : $"`{Cell(text)}`";

    private static string Count(long? rows) => rows is null ? "" : rows.Value.ToString("N0");

    private static string Size(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} kB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):N1} GB",
    };
}
