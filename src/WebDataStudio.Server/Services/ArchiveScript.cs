using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WebDataStudio.Server.Services;

/// An archive's rows as INSERT statements. Written out rather than executed: it goes to the editor
/// and through the same preview as any other change, which is the rule everywhere else in the
/// studio and there is no reason for archives to be the exception.
public static class ArchiveScript
{
    public static string Inserts(string engine, string table, IReadOnlyList<ArchiveColumn> columns,
        IEnumerable<object?[]> rows)
    {
        if (columns.Count == 0) throw new ArgumentException("this archive has no columns");

        var quote = Quoting(engine);
        var text = new StringBuilder();
        var names = string.Join(", ", columns.Select(column => quote(column.Name)));
        var written = 0;

        foreach (var row in rows)
        {
            var values = new List<string>(columns.Count);

            for (var index = 0; index < columns.Count; index++)
                values.Add(Literal(index < row.Length ? row[index] : null));

            text.AppendLine($"INSERT INTO {table} ({names}) VALUES ({string.Join(", ", values)});");
            written++;
        }

        return written == 0
            ? $"-- this archive holds no rows\n-- INSERT INTO {table} ({names}) VALUES (…);"
            : text.ToString().TrimEnd();
    }

    private static Func<string, string> Quoting(string engine) => engine switch
    {
        "mysql" => name => $"`{name.Replace("`", "``")}`",
        "sqlserver" => name => $"[{name.Replace("]", "]]")}]",
        _ => name => $"\"{name.Replace("\"", "\"\"")}\"",
    };

    /// A value as SQL. Everything that is not plainly a number or a boolean becomes a quoted string:
    /// a wrong guess here would be a wrong row, and the engine casts a string it can read.
    private static string Literal(object? value)
    {
        if (value is null) return "NULL";

        // Rows come back out of the file as JSON, so this is what a value actually is here.
        if (value is JsonElement element)
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => "NULL",
                JsonValueKind.True => "TRUE",
                JsonValueKind.False => "FALSE",
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.String => Text(element.GetString() ?? ""),
                // An object or an array was one value in the result; it goes back as its own text.
                _ => Text(element.GetRawText()),
            };

        return value switch
        {
            bool flag => flag ? "TRUE" : "FALSE",
            byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
            DateTime date => Text(date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
        };
    }

    private static string Text(string value) => "'" + value.Replace("'", "''") + "'";
}
