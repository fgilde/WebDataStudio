using System.Data.Common;
using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Editing;

/// One reversible step: the changes that put the data back where it was, and enough context to
/// tell a person what they would be undoing.
public sealed record UndoEntry(
    string Id, string ObjectRef, string Label, DateTimeOffset At, IReadOnlyList<RowChange> Changes);

/// The inverse of a change set. Built from the rows as they were *before* the change, read inside
/// the same transaction that applies it — anything read afterwards is somebody else's data.
public static class Undo
{
    /// The inverse changes, in no particular order: the script builder orders deletes, updates and
    /// inserts for itself. `before` is keyed by the index of the change it belongs to; a change with
    /// no entry there is one whose row could not be read, and it is skipped rather than guessed at.
    public static List<RowChange> BuildInverse(
        ChangeSet set, IReadOnlyList<string> keyColumns,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, object?>> before)
    {
        var inverse = new List<RowChange>();

        for (var index = 0; index < set.Changes.Count; index++)
        {
            var change = set.Changes[index];
            before.TryGetValue(index, out var row);

            switch (change.Kind)
            {
                // What was inserted goes away again — but only if the insert said what its key is.
                // A generated key is not in the request, and deleting by a guess is worse than
                // saying the step cannot be undone.
                case "insert":
                {
                    var key = Subset(change.Values, keyColumns);
                    if (keyColumns.Count > 0 && key.Count == keyColumns.Count)
                        inverse.Add(new RowChange("delete", key, new Dictionary<string, object?>()));
                    break;
                }

                // Back to the old values of exactly the columns that were written, addressed by the
                // same key. Columns nobody touched stay out of it.
                // An update addressed by where the row physically is cannot be undone: the
                // write moves the address, so the inverse would find nothing — or, worse, whatever
                // ends up at that address later. A delete still can: its inverse carries the whole
                // row and needs no address at all.
                case "update" when change.Key.ContainsKey(RowIdentity.AddressColumn):
                    break;

                case "update" when row is not null:
                {
                    var values = Subset(row, [.. change.Values.Keys]);
                    if (values.Count > 0) inverse.Add(new RowChange("update", change.Key, values));
                    break;
                }

                // A deleted row comes back whole.
                case "delete" when row is not null:
                    inverse.Add(new RowChange("insert", new Dictionary<string, object?>(),
                        Subset(row, [.. row.Keys])));
                    break;
            }
        }

        return inverse;
    }

    /// Reads the rows an update or a delete is about to change, so the inverse is built from what
    /// was actually there rather than from what the browser believed was there.
    public static async Task<Dictionary<int, IReadOnlyDictionary<string, object?>>> CaptureAsync(
        IDbSession session, DbTransaction? transaction, SqlDialect dialect, SchemaNodeRef target,
        ChangeSet set, CancellationToken ct)
    {
        var captured = new Dictionary<int, IReadOnlyDictionary<string, object?>>();
        var table = ChangeScriptBuilder.Qualify(target, dialect);

        for (var index = 0; index < set.Changes.Count; index++)
        {
            var change = set.Changes[index];
            if (change.Kind is not ("update" or "delete") || change.Key.Count == 0) continue;

            var predicates = new List<string>();
            await using var command = session.Connection.CreateCommand();
            command.Transaction = transaction;

            var i = 0;
            foreach (var (name, value) in change.Key)
            {
                predicates.Add(ChangeScriptBuilder.KeyPredicate(
                    dialect, name, $"{dialect.ParameterPrefix}k{i}"));
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"k{i}";
                parameter.Value = ChangeScriptBuilder.Normalize(value) ?? DBNull.Value;
                command.Parameters.Add(parameter);
                i++;
            }

            command.CommandText = $"SELECT * FROM {table} WHERE {string.Join(" AND ", predicates)}";

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) continue;

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var field = 0; field < reader.FieldCount; field++)
                row[reader.GetName(field)] = await reader.IsDBNullAsync(field, ct)
                    ? null
                    : reader.GetValue(field);

            captured[index] = row;
        }

        return captured;
    }

    /// What the undo entry is called in the UI. Counting the kinds beats storing a sentence.
    public static string Describe(ChangeSet set) => string.Join(", ", set.Changes
        .GroupBy(c => c.Kind)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}"));

    private static Dictionary<string, object?> Subset(
        IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> columns)
    {
        var subset = new Dictionary<string, object?>();

        foreach (var column in columns)
        {
            var found = row.Keys.FirstOrDefault(k => k.Equals(column, StringComparison.OrdinalIgnoreCase));
            if (found is not null) subset[column] = Plain(row[found]);
        }

        return subset;
    }

    /// Values survive a round trip through the workspace store, so they have to be plain
    /// JSON-able things rather than provider types.
    private static object? Plain(object? value) => value switch
    {
        JsonElement e => ChangeScriptBuilder.Normalize(e),
        DateTime d => d.ToString("O"),
        DateTimeOffset d => d.ToString("O"),
        byte[] b => Convert.ToBase64String(b),
        _ => value,
    };
}
