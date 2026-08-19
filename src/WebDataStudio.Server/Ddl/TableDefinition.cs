using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Ddl;

public sealed record ColumnDefinition(
    string Name, string Type, bool Nullable, string? Default, bool Identity,
    string? Comment, string? RenamedFrom = null);

public sealed record IndexDefinition(
    string Name, IReadOnlyList<string> Columns, bool Unique, string? Filter = null,
    IReadOnlyList<string>? IncludeColumns = null,
    /// A full-text index over the listed columns, written the way the engine spells it.
    bool FullText = false);

public enum ConstraintKind { PrimaryKey, Unique, Check, ForeignKey }

public sealed record ConstraintDefinition(
    string Name, ConstraintKind Kind, IReadOnlyList<string> Columns,
    string? Expression = null,
    string? ReferencedTable = null, IReadOnlyList<string>? ReferencedColumns = null,
    string OnDelete = "NO ACTION", string OnUpdate = "NO ACTION");

public sealed record TableDefinition(
    string Schema, string Name,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<IndexDefinition> Indexes,
    IReadOnlyList<ConstraintDefinition> Constraints,
    string? Comment)
{
    public static TableDefinition From(ObjectDetail detail)
    {
        var schema = detail.Ref.Path.Count > 1 ? detail.Ref.Path[0] : "";

        var columns = detail.Columns
            .OrderBy(c => c.Position)
            .Select(c => new ColumnDefinition(c.Name, c.DataType, c.Nullable, c.Default, c.IsIdentity, c.Comment))
            .ToList();

        // The primary key comes back as a constraint, not as an index: the designer edits it as one.
        var primaryKey = detail.Indexes.FirstOrDefault(i => i.Primary);
        var constraints = new List<ConstraintDefinition>();

        if (primaryKey is not null && primaryKey.Columns.Count > 0)
        {
            constraints.Add(new ConstraintDefinition(primaryKey.Name, ConstraintKind.PrimaryKey, primaryKey.Columns));
        }
        else if (detail.Columns.Any(c => c.IsPrimaryKey))
        {
            // Some engines report the key only on the columns, not as an index of its own.
            constraints.Add(new ConstraintDefinition($"pk_{detail.Ref.Name}", ConstraintKind.PrimaryKey,
                detail.Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Position).Select(c => c.Name).ToList()));
        }

        constraints.AddRange(detail.ForeignKeys.Select(f => new ConstraintDefinition(
            f.Name, ConstraintKind.ForeignKey, f.Columns, null,
            f.ReferencedTable, f.ReferencedColumns, f.OnDelete, f.OnUpdate)));

        var indexes = detail.Indexes
            .Where(i => !i.Primary)
            .Select(i => new IndexDefinition(i.Name, i.Columns, i.Unique, i.Filter, null, i.FullText))
            .ToList();

        return new TableDefinition(schema, detail.Ref.Name, columns, indexes, constraints, detail.Comment);
    }
}

public sealed record ColumnChange(ColumnDefinition Column, ColumnDefinition? Before);

public sealed record TableChange(
    IReadOnlyList<ColumnDefinition> AddedColumns,
    IReadOnlyList<ColumnDefinition> DroppedColumns,
    IReadOnlyList<ColumnChange> AlteredColumns,
    IReadOnlyList<ColumnChange> RenamedColumns,
    IReadOnlyList<IndexDefinition> AddedIndexes,
    IReadOnlyList<IndexDefinition> DroppedIndexes,
    IReadOnlyList<ConstraintDefinition> AddedConstraints,
    IReadOnlyList<ConstraintDefinition> DroppedConstraints,
    bool CommentChanged,
    string? Comment)
{
    public bool IsEmpty =>
        AddedColumns.Count == 0 && DroppedColumns.Count == 0 && AlteredColumns.Count == 0
        && RenamedColumns.Count == 0 && AddedIndexes.Count == 0 && DroppedIndexes.Count == 0
        && AddedConstraints.Count == 0 && DroppedConstraints.Count == 0 && !CommentChanged;
}

public static class TableDiff
{
    public static TableChange Compute(TableDefinition before, TableDefinition after)
    {
        var beforeByName = before.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var renamed = new List<ColumnChange>();
        var altered = new List<ColumnChange>();
        var added = new List<ColumnDefinition>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in after.Columns)
        {
            // A renamed column carries where it came from; without that marker a rename is
            // indistinguishable from a drop plus an add, and would lose the data.
            if (column.RenamedFrom is { Length: > 0 } origin && beforeByName.TryGetValue(origin, out var source))
            {
                handled.Add(origin);
                renamed.Add(new ColumnChange(column, source));
                if (Differs(source, column)) altered.Add(new ColumnChange(column, source));
                continue;
            }

            if (beforeByName.TryGetValue(column.Name, out var existing))
            {
                handled.Add(column.Name);
                if (Differs(existing, column)) altered.Add(new ColumnChange(column, existing));
                continue;
            }

            added.Add(column);
        }

        var dropped = before.Columns.Where(c => !handled.Contains(c.Name)).ToList();

        // Indexes and constraints compare by shape, not by name: renaming an index is not a change
        // to what the database enforces.
        var addedIndexes = after.Indexes.Where(a => !before.Indexes.Any(b => SameShape(a, b))).ToList();
        var droppedIndexes = before.Indexes.Where(b => !after.Indexes.Any(a => SameShape(a, b))).ToList();

        var addedConstraints = after.Constraints.Where(a => !before.Constraints.Any(b => SameShape(a, b))).ToList();
        var droppedConstraints = before.Constraints.Where(b => !after.Constraints.Any(a => SameShape(a, b))).ToList();

        var commentChanged = (before.Comment ?? "") != (after.Comment ?? "");

        return new TableChange(added, dropped, altered, renamed, addedIndexes, droppedIndexes,
            addedConstraints, droppedConstraints, commentChanged, after.Comment);
    }

    private static bool Differs(ColumnDefinition a, ColumnDefinition b) =>
        !a.Type.Equals(b.Type, StringComparison.OrdinalIgnoreCase)
        || a.Nullable != b.Nullable
        || (a.Default ?? "") != (b.Default ?? "")
        || a.Identity != b.Identity
        || (a.Comment ?? "") != (b.Comment ?? "");

    private static bool SameShape(IndexDefinition a, IndexDefinition b) =>
        a.Unique == b.Unique
        && (a.Filter ?? "") == (b.Filter ?? "")
        && a.Columns.SequenceEqual(b.Columns, StringComparer.OrdinalIgnoreCase);

    private static bool SameShape(ConstraintDefinition a, ConstraintDefinition b) =>
        a.Kind == b.Kind
        && a.Columns.SequenceEqual(b.Columns, StringComparer.OrdinalIgnoreCase)
        && (a.Expression ?? "") == (b.Expression ?? "")
        && (a.ReferencedTable ?? "") == (b.ReferencedTable ?? "")
        && (a.ReferencedColumns ?? []).SequenceEqual(b.ReferencedColumns ?? [], StringComparer.OrdinalIgnoreCase);
}
