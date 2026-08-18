using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Editing;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;

namespace WebDataStudio.Server.Compare;

public sealed record RowDifference(
    IReadOnlyList<object?> Key,
    IReadOnlyList<string> ChangedColumns,
    IReadOnlyList<object?> SourceRow,
    IReadOnlyList<object?> TargetRow);

public sealed record DataComparison(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Missing,
    IReadOnlyList<IReadOnlyList<object?>> Extra,
    IReadOnlyList<RowDifference> Different,
    int Identical,
    bool Truncated);

/// Walks both sides ordered by the key and compares in lockstep — a merge join, so memory stays
/// bounded by one row per side rather than by table size.
public static class DataComparer
{
    public static async Task<DataComparison> CompareAsync(
        IDbDriver sourceDriver, IDbSession sourceSession,
        IDbDriver targetDriver, IDbSession targetSession,
        SchemaNodeRef sourceRef, SchemaNodeRef targetRef,
        IReadOnlyList<string> keyColumns, int maxRows, CancellationToken ct)
    {
        if (keyColumns.Count == 0)
            throw new InvalidOperationException("comparing data needs key columns to match rows by");

        var sourceRows = ReadAsync(sourceDriver, sourceSession, sourceRef, keyColumns, maxRows, ct);
        var targetRows = ReadAsync(targetDriver, targetSession, targetRef, keyColumns, maxRows, ct);

        await using var left = sourceRows.GetAsyncEnumerator(ct);
        await using var right = targetRows.GetAsyncEnumerator(ct);

        var missing = new List<IReadOnlyList<object?>>();
        var extra = new List<IReadOnlyList<object?>>();
        var different = new List<RowDifference>();
        var identical = 0;
        var compared = 0;
        var columns = new List<string>();

        var hasLeft = await left.MoveNextAsync();
        var hasRight = await right.MoveNextAsync();

        while (hasLeft || hasRight)
        {
            if (compared++ >= maxRows) return Build(true);

            if (!hasRight)
            {
                columns = left.Current.Columns.ToList();
                missing.Add(left.Current.Values);
                hasLeft = await left.MoveNextAsync();
                continue;
            }

            if (!hasLeft)
            {
                columns = right.Current.Columns.ToList();
                extra.Add(right.Current.Values);
                hasRight = await right.MoveNextAsync();
                continue;
            }

            columns = left.Current.Columns.ToList();
            var order = CompareKeys(left.Current.Key, right.Current.Key);

            if (order < 0) { missing.Add(left.Current.Values); hasLeft = await left.MoveNextAsync(); }
            else if (order > 0) { extra.Add(right.Current.Values); hasRight = await right.MoveNextAsync(); }
            else
            {
                var changed = ChangedColumns(left.Current, right.Current);
                if (changed.Count == 0) identical++;
                else different.Add(new RowDifference(left.Current.Key, changed,
                    left.Current.Values, right.Current.Values));

                hasLeft = await left.MoveNextAsync();
                hasRight = await right.MoveNextAsync();
            }
        }

        return Build(false);

        DataComparison Build(bool truncated) =>
            new(columns, missing, extra, different, identical, truncated);
    }

    /// INSERT for what is missing, UPDATE for what differs, DELETE for what is extra.
    public static IReadOnlyList<string> SyncScript(DataComparison comparison, SchemaNodeRef targetRef,
        IReadOnlyList<string> keyColumns, SqlDialect dialect)
    {
        var table = ChangeScriptBuilder.Qualify(targetRef, dialect);
        var statements = new List<string>();
        var columns = comparison.Columns;

        foreach (var row in comparison.Missing)
            statements.Add(
                $"INSERT INTO {table} ({string.Join(", ", columns.Select(dialect.QuoteIdentifier))}) " +
                $"VALUES ({string.Join(", ", row.Select(v => SqlLiteral.Render(v, dialect)))});");

        foreach (var difference in comparison.Different)
        {
            var assignments = difference.ChangedColumns.Select(c =>
            {
                var index = columns.ToList().FindIndex(x => x.Equals(c, StringComparison.OrdinalIgnoreCase));
                return $"{dialect.QuoteIdentifier(c)} = {SqlLiteral.Render(difference.SourceRow[index], dialect)}";
            });

            statements.Add($"UPDATE {table} SET {string.Join(", ", assignments)} " +
                           $"WHERE {Predicate(difference.Key, keyColumns, dialect)};");
        }

        foreach (var row in comparison.Extra)
        {
            var key = keyColumns
                .Select(k => row[columns.ToList().FindIndex(x => x.Equals(k, StringComparison.OrdinalIgnoreCase))])
                .ToList();

            statements.Add($"DELETE FROM {table} WHERE {Predicate(key, keyColumns, dialect)};");
        }

        return statements;
    }

    private static string Predicate(IReadOnlyList<object?> key, IReadOnlyList<string> keyColumns,
        SqlDialect dialect) =>
        string.Join(" AND ", keyColumns.Select((c, i) =>
            $"{dialect.QuoteIdentifier(c)} = {SqlLiteral.Render(key[i], dialect)}"));

    private sealed record ComparedRow(
        IReadOnlyList<string> Columns, IReadOnlyList<object?> Values, IReadOnlyList<object?> Key);

    private static async IAsyncEnumerable<ComparedRow> ReadAsync(IDbDriver driver, IDbSession session,
        SchemaNodeRef target, IReadOnlyList<string> keyColumns, int maxRows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var table = ChangeScriptBuilder.Qualify(target, driver.Dialect);
        var order = string.Join(", ", keyColumns.Select(driver.Dialect.QuoteIdentifier));
        var request = new ScriptRequest($"SELECT * FROM {table} ORDER BY {order}", maxRows, 300);

        var columns = new List<string>();
        var keyIndexes = new List<int>();

        await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns = c.Items.Select(x => x.Name).ToList();
                    keyIndexes = keyColumns
                        .Select(k => columns.FindIndex(x => x.Equals(k, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items)
                        yield return new ComparedRow(columns, row,
                            keyIndexes.Select(i => i >= 0 && i < row.Length ? row[i] : null).ToList());
                    break;

                case ResultChunk.Error error:
                    throw new InvalidOperationException(error.Text);
            }
        }
    }

    private static int CompareKeys(IReadOnlyList<object?> a, IReadOnlyList<object?> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var order = CompareValues(a[i], b[i]);
            if (order != 0) return order;
        }
        return a.Count.CompareTo(b.Count);
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        // Numbers compare numerically even when the drivers hand them over as different CLR types.
        if (double.TryParse(a.ToString(), out var x) && double.TryParse(b.ToString(), out var y))
            return x.CompareTo(y);

        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    private static List<string> ChangedColumns(ComparedRow source, ComparedRow target)
    {
        var changed = new List<string>();

        for (var i = 0; i < source.Columns.Count && i < target.Values.Count; i++)
            if (CompareValues(source.Values[i], target.Values[i]) != 0)
                changed.Add(source.Columns[i]);

        return changed;
    }
}
