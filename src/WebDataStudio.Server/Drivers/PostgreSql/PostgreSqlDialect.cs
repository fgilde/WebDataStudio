using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.PostgreSql;

public sealed class PostgreSqlDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public override string ParameterPrefix => "@";
    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";

    public override string? RowAddress => "ctid";
    public override string RowAddressPredicate(string parameter) => $"ctid = CAST({parameter} AS tid)";

    /// `col #>> '{a,b}'` — the text at a path, for json and jsonb alike. An array step reads the
    /// first element: a flattened column holds one value, and "the first tag" is the useful one.
    public override string JsonPath(string column, string path)
    {
        var steps = path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(step => step.EndsWith("[]", StringComparison.Ordinal)
                ? new[] { step[..^2], "0" }
                : [step])
            .Select(step => step.Replace("\"", "\\\""));

        return $"{column}::jsonb #>> '{{{string.Join(",", steps)}}}'";
    }
}
