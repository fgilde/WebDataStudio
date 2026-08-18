using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;

namespace WebDataStudio.Server.Editing;

public sealed record ScriptStatement(
    string Sql, IReadOnlyDictionary<string, object?> Parameters, int ChangeIndex, bool Destructive);

public sealed record ChangeScript(IReadOnlyList<ScriptStatement> Statements, string Text);

public static class ChangeScriptBuilder
{
    /// Order matters: deletes first, then updates, then inserts, so deleting a row and inserting a
    /// new one with the same key inside a single change set cannot collide.
    private static readonly string[] KindOrder = ["delete", "update", "insert"];

    public static ChangeScript Build(ChangeSet changeSet, ObjectDetail detail, SqlDialect dialect)
    {
        var table = Qualify(detail.Ref, dialect);
        var statements = new List<ScriptStatement>();

        var ordered = changeSet.Changes
            .Select((change, index) => (change, index))
            .OrderBy(x => Array.IndexOf(KindOrder, x.change.Kind));

        foreach (var (change, index) in ordered)
        {
            var statement = change.Kind switch
            {
                "insert" => Insert(table, change, index, dialect),
                "update" => Update(table, change, index, dialect),
                "delete" => Delete(table, change, index, dialect),
                _ => throw new InvalidOperationException($"unknown change kind '{change.Kind}'"),
            };
            statements.Add(statement);
        }

        var text = string.Join("\n", statements.Select(s => Readable(s, dialect)));
        return new ChangeScript(statements, text);
    }

    private static ScriptStatement Insert(string table, RowChange change, int index, SqlDialect dialect)
    {
        var columns = change.Values.Keys.ToList();
        var parameters = new Dictionary<string, object?>();

        for (var i = 0; i < columns.Count; i++) parameters[$"p{i}"] = change.Values[columns[i]];

        var names = string.Join(", ", columns.Select(dialect.QuoteIdentifier));
        var values = string.Join(", ", columns.Select((_, i) => $"{dialect.ParameterPrefix}p{i}"));

        return new ScriptStatement($"INSERT INTO {table} ({names}) VALUES ({values})", parameters, index, false);
    }

    private static ScriptStatement Update(string table, RowChange change, int index, SqlDialect dialect)
    {
        var setColumns = change.Values.Keys.ToList();
        var keyColumns = change.Key.Keys.ToList();
        var parameters = new Dictionary<string, object?>();

        var assignments = new List<string>();
        for (var i = 0; i < setColumns.Count; i++)
        {
            parameters[$"p{i}"] = change.Values[setColumns[i]];
            assignments.Add($"{dialect.QuoteIdentifier(setColumns[i])} = {dialect.ParameterPrefix}p{i}");
        }

        var predicates = new List<string>();
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var name = $"k{i}";
            parameters[name] = change.Key[keyColumns[i]];
            predicates.Add($"{dialect.QuoteIdentifier(keyColumns[i])} = {dialect.ParameterPrefix}{name}");
        }

        var sql = $"UPDATE {table} SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", predicates)}";
        return new ScriptStatement(sql, parameters, index, false);
    }

    private static ScriptStatement Delete(string table, RowChange change, int index, SqlDialect dialect)
    {
        var keyColumns = change.Key.Keys.ToList();
        var parameters = new Dictionary<string, object?>();

        var predicates = new List<string>();
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var name = $"k{i}";
            parameters[name] = change.Key[keyColumns[i]];
            predicates.Add($"{dialect.QuoteIdentifier(keyColumns[i])} = {dialect.ParameterPrefix}{name}");
        }

        return new ScriptStatement($"DELETE FROM {table} WHERE {string.Join(" AND ", predicates)}",
            parameters, index, true);
    }

    /// The human-readable form shown in the preview. Rendered through the same literal writer the
    /// SQL exporter uses, so what the user approves matches what executes.
    private static string Readable(ScriptStatement statement, SqlDialect dialect)
    {
        var sql = statement.Sql;

        // Longest first: p10 must not be replaced by the p1 substitution.
        foreach (var (name, value) in statement.Parameters.OrderByDescending(p => p.Key.Length))
            sql = sql.Replace($"{dialect.ParameterPrefix}{name}", SqlLiteral.Render(Normalize(value), dialect));

        return sql + ";";
    }

    /// JSON values arrive as JsonElement from the endpoint; unwrap them before rendering.
    internal static object? Normalize(object? value) => value switch
    {
        System.Text.Json.JsonElement e => e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            _ => e.GetString(),
        },
        _ => value,
    };

    internal static string Qualify(SchemaNodeRef target, SqlDialect dialect) =>
        target.Path.Count > 1
            ? $"{dialect.QuoteIdentifier(target.Path[0])}.{dialect.QuoteIdentifier(target.Name)}"
            : dialect.QuoteIdentifier(target.Name);
}
