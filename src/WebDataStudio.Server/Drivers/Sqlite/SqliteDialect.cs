using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.Sqlite;

public sealed class SqliteDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public override string ParameterPrefix => "$";
    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";
}
