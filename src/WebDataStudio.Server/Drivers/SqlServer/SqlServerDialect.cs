using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.SqlServer;

public sealed class SqlServerDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";
    public override string ParameterPrefix => "@";
    public override bool UsesGoBatchSeparator => true;

    // SQL Server requires an ORDER BY for OFFSET/FETCH; a stable no-op ordering keeps it legal.
    public override string Paginate(string sql, int offset, int limit) =>
        sql.Contains("order by", StringComparison.OrdinalIgnoreCase)
            ? $"{sql} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"
            : $"{sql} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
}
