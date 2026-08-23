using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.Sqlite;

public sealed class SqliteDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public override string ParameterPrefix => "$";
    // SQLite keeps a timestamp as ISO text, where a comparison already sorts correctly. Casting it
    // would be worse than useless: CAST('2026-08-23' AS timestamp) has numeric affinity here, and
    // that reads the string as the number 2026.
    public override string TimestampCast => "{0}";

    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";
}
