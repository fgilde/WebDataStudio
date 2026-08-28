using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// The statements that would carry another database from the snapshot's schema to this one.
///
/// The drift report answers "what moved since Monday". This is the other half of that question —
/// "and what do I run on the other machine" — built from the live schema rather than from the
/// snapshot's summary: the snapshot says *which* table changed, the database says what it looks
/// like now.
///
/// What it writes: the tables that appeared, as they are now; the columns and indexes that
/// appeared; the drops for what is gone. What it deliberately leaves to a person: a column whose
/// type or nullability moved. The snapshot keeps the type, so the change is *reported* — but
/// turning it into an ALTER means deciding whether the data still fits, and a migration that
/// silently truncates is worse than a line saying "look at this one".
public static class DriftMigration
{
    public sealed record DriftScript(
        IReadOnlyList<DdlStatement> Statements,
        /// What the studio saw but will not write. Shown next to the script rather than dropped.
        IReadOnlyList<string> NeedsAPerson)
    {
        public string Text => string.Join("\n", Statements.Select(statement => statement.Sql));
        public bool Destructive => Statements.Any(statement => statement.Destructive);
    }

    public static async Task<DriftScript> BuildAsync(IDbDriver driver, IDbSession session,
        DdlWriterBase writer, SchemaShape? before, SchemaShape after, CancellationToken ct)
    {
        if (before is null)
            return new DriftScript([], ["there is no earlier snapshot to compare against"]);

        var statements = new List<DdlStatement>();
        var manual = new List<string>();

        var was = before.Tables.ToDictionary(table => table.Ref, StringComparer.Ordinal);
        var now = after.Tables.ToDictionary(table => table.Ref, StringComparer.Ordinal);

        await AddedTablesAsync(driver, session, writer, was, now, statements, manual, ct);
        DroppedTables(writer, was, now, statements);
        await ChangedTablesAsync(driver, session, writer, was, now, statements, manual, ct);

        return new DriftScript(statements, manual);
    }

    private static async Task AddedTablesAsync(IDbDriver driver, IDbSession session,
        DdlWriterBase writer, Dictionary<string, TableShape> was, Dictionary<string, TableShape> now,
        List<DdlStatement> statements, List<string> manual, CancellationToken ct)
    {
        foreach (var name in now.Keys.Where(key => !was.ContainsKey(key)).Order(StringComparer.Ordinal))
        {
            var target = SchemaNodeRef.Parse(name);

            if (target.Kind != SchemaNodeKind.Table)
            {
                // A view's text is not in the snapshot, and inventing one would be worse than saying
                // where to look.
                manual.Add($"{target.Name} is new — its definition is not in the snapshot");
                continue;
            }

            try
            {
                var detail = await driver.DescribeAsync(session, target, ct);
                statements.AddRange(writer.CreateTable(TableDefinition.From(detail)));
            }
            catch (Exception e)
            {
                manual.Add($"{target.Name} is new, and could not be read: {e.Message}");
            }
        }
    }

    private static void DroppedTables(DdlWriterBase writer, Dictionary<string, TableShape> was,
        Dictionary<string, TableShape> now, List<DdlStatement> statements)
    {
        foreach (var name in was.Keys.Where(key => !now.ContainsKey(key)).Order(StringComparer.Ordinal))
        {
            var target = SchemaNodeRef.Parse(name);
            var schema = target.Path.Count > 1 ? target.Path[0] : "";

            statements.AddRange(target.Kind == SchemaNodeKind.Table
                ? writer.DropTable(schema, target.Name)
                : writer.DropObject(target));
        }
    }

    private static async Task ChangedTablesAsync(IDbDriver driver, IDbSession session,
        DdlWriterBase writer, Dictionary<string, TableShape> was, Dictionary<string, TableShape> now,
        List<DdlStatement> statements, List<string> manual, CancellationToken ct)
    {
        foreach (var (name, table) in now.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!was.TryGetValue(name, out var old)) continue;

            var target = SchemaNodeRef.Parse(name);
            if (target.Kind != SchemaNodeKind.Table) continue;

            var schema = target.Path.Count > 1 ? target.Path[0] : "";

            var goneColumns = Names(old.Columns).Except(Names(table.Columns), Ordinal).ToList();
            var newColumns = Names(table.Columns).Except(Names(old.Columns), Ordinal).ToList();
            var newIndexes = table.Indexes.Except(old.Indexes, Ordinal).Select(IndexName).ToList();
            var goneIndexes = old.Indexes.Except(table.Indexes, Ordinal).Select(IndexName)
                .Except(newIndexes, Ordinal).ToList();

            // Same name on both sides, different text: the type or the nullability moved.
            foreach (var column in table.Columns
                .Where(column => !newColumns.Contains(Name(column), Ordinal))
                .Where(column => !old.Columns.Contains(column, Ordinal))
                .Select(Name))
                manual.Add($"{target.Name}.{column} changed type or nullability — check the data first");

            if (goneColumns.Count == 0 && newColumns.Count == 0
                && newIndexes.Count == 0 && goneIndexes.Count == 0)
                continue;

            TableDefinition? live = null;

            if (newColumns.Count > 0 || newIndexes.Count > 0)
                try { live = TableDefinition.From(await driver.DescribeAsync(session, target, ct)); }
                catch (Exception e) { manual.Add($"{target.Name} changed, and could not be read: {e.Message}"); }

            if (live is not null)
            {
                var added = live.Columns.Where(column => newColumns.Contains(column.Name, Ordinal)).ToList();

                if (added.Count > 0)
                    statements.AddRange(writer.AlterTable(live, Change(added: added)));

                foreach (var index in live.Indexes.Where(index => newIndexes.Contains(index.Name, Ordinal)))
                    statements.AddRange(writer.CreateIndex(schema, target.Name, index));
            }

            foreach (var index in goneIndexes)
                statements.AddRange(writer.DropIndex(schema, target.Name, index));

            if (goneColumns.Count > 0)
                statements.AddRange(writer.AlterTable(
                    new TableDefinition(schema, target.Name, [], [], [], null),
                    Change(dropped: goneColumns
                        .Select(column => new ColumnDefinition(column, "", true, null, false, null))
                        .ToList())));
        }
    }

    private static TableChange Change(
        IReadOnlyList<ColumnDefinition>? added = null,
        IReadOnlyList<ColumnDefinition>? dropped = null) =>
        new(added ?? [], dropped ?? [], [], [], [], [], [], [], false, null);

    private static StringComparer Ordinal => StringComparer.Ordinal;

    private static IEnumerable<string> Names(IEnumerable<string> columns) => columns.Select(Name);

    /// "id integer null pk" is one column; its name is the first word.
    private static string Name(string column) => column.Split(' ', 2)[0];

    /// "ix_orders(customer_id) unique" is one index; its name stands before the bracket.
    private static string IndexName(string index) => index.Split('(', 2)[0];
}
