using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.MySql;

public sealed class MySqlDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "`" + name.Replace("`", "``") + "`";
    public override string TextType => "CHAR";

    // MySQL spells the number type DECIMAL and has no unqualified precision worth relying on.
    public override string NumberCast => "CAST({0} AS DECIMAL(38,10))";

    public override string TimestampCast => "CAST({0} AS DATETIME)";

    public override string ParameterPrefix => "@";
    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";

    /// MySQL unquotes explicitly; without it every extracted string arrives with its quotes.
    public override string JsonPath(string column, string path) =>
        $"JSON_UNQUOTE(JSON_EXTRACT({column}, '{JsonPathLiteral(path).Replace("[*]", "[0]")}'))";
}
