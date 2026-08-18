using WebDataStudio.Server.Ddl;

namespace WebDataStudio.Server.Compare;

public sealed record ChangedTable(string Name, TableChange Change);

public sealed record SchemaComparison(
    IReadOnlyList<string> TablesOnlyInSource,
    IReadOnlyList<string> TablesOnlyInTarget,
    IReadOnlyList<ChangedTable> ChangedTables,
    IReadOnlyList<string> IdenticalTables);

/// Pure over two definition lists, so a PostgreSQL schema can be compared with a SQL Server one:
/// both sides are already expressed in the neutral definition model.
public static class SchemaComparer
{
    public static SchemaComparison Compare(
        IReadOnlyList<TableDefinition> source, IReadOnlyList<TableDefinition> target)
    {
        var targetByName = target.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var sourceByName = source.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        var onlyInSource = source
            .Where(t => !targetByName.ContainsKey(t.Name))
            .Select(t => t.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var onlyInTarget = target
            .Where(t => !sourceByName.ContainsKey(t.Name))
            .Select(t => t.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changed = new List<ChangedTable>();
        var identical = new List<string>();

        foreach (var table in source.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!targetByName.TryGetValue(table.Name, out var other)) continue;

            // Target first: the diff describes what has to happen to the target to match the source.
            var change = TableDiff.Compute(other, table);
            if (change.IsEmpty) identical.Add(table.Name);
            else changed.Add(new ChangedTable(table.Name, change));
        }

        return new SchemaComparison(onlyInSource, onlyInTarget, changed, identical);
    }

    /// The script that makes the target match the source, written in the target engine's dialect.
    public static IReadOnlyList<DdlStatement> SyncScript(
        SchemaComparison comparison,
        IReadOnlyList<TableDefinition> source,
        IReadOnlyList<TableDefinition> target,
        DdlWriterBase writer)
    {
        var statements = new List<DdlStatement>();
        var sourceByName = source.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var targetByName = target.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in comparison.TablesOnlyInSource)
            statements.AddRange(writer.CreateTable(sourceByName[name]));

        foreach (var changed in comparison.ChangedTables)
            statements.AddRange(writer.AlterTable(targetByName[changed.Name], changed.Change));

        foreach (var name in comparison.TablesOnlyInTarget)
            statements.AddRange(writer.DropTable(targetByName[name].Schema, name));

        return statements;
    }
}
