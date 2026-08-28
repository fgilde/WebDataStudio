using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.SqlServer;

public sealed class SqlServerDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";
    public override string TextType => "NVARCHAR(MAX)";

    public override string NumberCast => "CAST({0} AS DECIMAL(38,10))";

    // DATETIME2, not DATETIME: the older type cannot hold what an ISO string can say.
    public override string TimestampCast => "CAST({0} AS DATETIME2)";

    public override string ParameterPrefix => "@";
    public override bool UsesGoBatchSeparator => true;

    // SQL Server requires an ORDER BY for OFFSET/FETCH; a stable no-op ordering keeps it legal.
    public override string Paginate(string sql, int offset, int limit) =>
        sql.Contains("order by", StringComparison.OrdinalIgnoreCase)
            ? $"{sql} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"
            : $"{sql} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";

    /// JSON_VALUE reads a scalar; an array step takes the first element, because a flattened column
    /// holds one value.
    public override string JsonPath(string column, string path) =>
        $"JSON_VALUE({column}, '{JsonPathLiteral(path).Replace("[*]", "[0]")}')";

}
