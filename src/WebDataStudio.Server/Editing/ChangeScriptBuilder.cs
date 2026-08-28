using System.Globalization;
using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;

namespace WebDataStudio.Server.Editing;

public sealed record ScriptStatement(
    string Sql, IReadOnlyDictionary<string, object?> Parameters, int ChangeIndex, bool Destructive);

public sealed record ChangeScript(IReadOnlyList<ScriptStatement> Statements, string Text);

public static partial class ChangeScriptBuilder
{
    /// Order matters: deletes first, then updates, then inserts, so deleting a row and inserting a
    /// new one with the same key inside a single change set cannot collide.
    private static readonly string[] KindOrder = ["delete", "update", "insert"];

    public static ChangeScript Build(ChangeSet changeSet, ObjectDetail detail, SqlDialect dialect)
    {
        var table = Qualify(detail.Ref, dialect);
        var statements = new List<ScriptStatement>();

        // What each column is declared as. A value travels to the engine as a string, and a string
        // is not a date: PostgreSQL says so rather than guessing, so the statement has to.
        var types = detail.Columns.ToDictionary(
            column => column.Name, column => column.DataType, StringComparer.OrdinalIgnoreCase);

        var ordered = changeSet.Changes
            .Select((change, index) => (change, index))
            .OrderBy(x => Array.IndexOf(KindOrder, x.change.Kind));

        foreach (var (change, index) in ordered)
        {
            var statement = change.Kind switch
            {
                "insert" => Insert(table, change, index, dialect, types),
                "update" => Update(table, change, index, dialect, types),
                "delete" => Delete(table, change, index, dialect, types),
                _ => throw new InvalidOperationException($"unknown change kind '{change.Kind}'"),
            };
            statements.Add(statement);
        }

        var text = string.Join("\n", statements.Select(s => Readable(s, dialect)));
        return new ChangeScript(statements, text);
    }

    private static ScriptStatement Insert(string table, RowChange change, int index,
        SqlDialect dialect, IReadOnlyDictionary<string, string> types)
    {
        var columns = change.Values.Keys.ToList();
        var parameters = new Dictionary<string, object?>();

        var names = string.Join(", ", columns.Select(dialect.QuoteIdentifier));
        var values = string.Join(", ", columns.Select((column, i) =>
            Bind(dialect, parameters, $"p{i}", Type(types, column), change.Values[column])));

        return new ScriptStatement($"INSERT INTO {table} ({names}) VALUES ({values})", parameters, index, false);
    }

    private static ScriptStatement Update(string table, RowChange change, int index,
        SqlDialect dialect, IReadOnlyDictionary<string, string> types)
    {
        var setColumns = change.Values.Keys.ToList();
        var keyColumns = change.Key.Keys.ToList();
        var parameters = new Dictionary<string, object?>();

        var assignments = new List<string>();
        for (var i = 0; i < setColumns.Count; i++)
            assignments.Add($"{dialect.QuoteIdentifier(setColumns[i])} = " +
                Bind(dialect, parameters, $"p{i}", Type(types, setColumns[i]), change.Values[setColumns[i]]));

        var predicates = new List<string>();
        for (var i = 0; i < keyColumns.Count; i++)
            predicates.Add($"{dialect.QuoteIdentifier(keyColumns[i])} = " +
                Bind(dialect, parameters, $"k{i}", Type(types, keyColumns[i]), change.Key[keyColumns[i]]));

        var sql = $"UPDATE {table} SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", predicates)}";
        return new ScriptStatement(sql, parameters, index, false);
    }

    private static ScriptStatement Delete(string table, RowChange change, int index,
        SqlDialect dialect, IReadOnlyDictionary<string, string> types)
    {
        var keyColumns = change.Key.Keys.ToList();
        var parameters = new Dictionary<string, object?>();

        var predicates = new List<string>();
        for (var i = 0; i < keyColumns.Count; i++)
            predicates.Add($"{dialect.QuoteIdentifier(keyColumns[i])} = " +
                Bind(dialect, parameters, $"k{i}", Type(types, keyColumns[i]), change.Key[keyColumns[i]]));

        return new ScriptStatement($"DELETE FROM {table} WHERE {string.Join(" AND ", predicates)}",
            parameters, index, true);
    }

    private static string? Type(IReadOnlyDictionary<string, string> types, string column) =>
        types.TryGetValue(column, out var type) ? type : null;

    /// What goes into the statement for one value, and what goes into the parameter list for it.
    ///
    /// Almost always a parameter. The exception is a file somebody put in a binary cell: parameters
    /// travel as text (see ScriptRequest), and text in a bytea column would be written as the
    /// *characters* "0x89504e47…" without a complaint. Bytes are written as this engine's own binary
    /// literal instead — safe to interpolate, because hex is all it can be.
    private static string Bind(SqlDialect dialect, Dictionary<string, object?> parameters,
        string name, string? declaredType, object? value)
    {
        if (declaredType is { Length: > 0 } && Binary(declaredType.ToLowerInvariant())
            && Normalize(value) is string text && BinaryValue.Parse(text) is { } bytes)
            return BinaryValue.Literal(bytes, dialect);

        parameters[name] = value;
        return Placeholder(dialect, name, declaredType, value);
    }

    /// The parameter, cast to what the column actually is when that is needed.
    ///
    /// Parameters reach the engine as strings (see ScriptRequest), and a string is not a date, a
    /// number or a uuid. PostgreSQL refuses `date = text` rather than guessing — which is the right
    /// call, and the reason "generate rows" on a table with a date column used to come back with
    /// "column is of type date but expression is of type text". So the statement says what the value
    /// is, using the column's own declared type.
    internal static string Placeholder(SqlDialect dialect, string name, string? declaredType,
        object? value)
    {
        var parameter = dialect.ParameterPrefix + name;

        // Only a string needs saying: a real number, boolean or DateTime is already typed, and a
        // null carries no type at all.
        if (Normalize(value) is not string) return parameter;
        if (declaredType is null or { Length: 0 }) return parameter;

        var type = declaredType.ToLowerInvariant();

        // Text into a text column is the common case and needs nothing.
        if (Textual(type)) return parameter;

        // Binary is not text that happens to look odd; casting a string into it would write
        // nonsense where an error is more honest.
        if (Binary(type)) return parameter;

        if (type.Contains("date") || type.Contains("time"))
            return string.Format(CultureInfo.InvariantCulture, dialect.TimestampCast, parameter);

        if (Numeric(type))
            return string.Format(CultureInfo.InvariantCulture, dialect.NumberCast, parameter);

        // Everything else — uuid, an enum, json, an interval — is cast to its own declared type.
        // Only when that reads as a type name though: a catalogue can answer with a category
        // ("USER-DEFINED") rather than a type, and casting to that is a syntax error where doing
        // nothing is merely the old behaviour.
        return TypeName().IsMatch(declaredType)
            ? $"CAST({parameter} AS {declaredType})"
            : parameter;
    }

    /// A plain type name, with an optional precision or an array suffix: `uuid`, `mood`,
    /// `numeric(10,2)`, `integer[]`.
    [GeneratedRegex(@"^[a-z_][a-z0-9_]*(\s*\(\s*\d+\s*(,\s*\d+\s*)?\))?(\[\])?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TypeName();

    private static bool Textual(string type) =>
        type.Contains("char") || type.Contains("text") || type.Contains("clob")
        || type is "string" or "name";

    private static bool Binary(string type) =>
        type.Contains("binary") || type.Contains("blob") || type.Contains("bytea")
        || type.Contains("image") || type is "raw" or "long raw";

    private static bool Numeric(string type) =>
        type.Contains("int") || type.Contains("dec") || type.Contains("num")
        || type.Contains("real") || type.Contains("double") || type.Contains("float")
        || type.Contains("money") || type.Contains("serial");

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
