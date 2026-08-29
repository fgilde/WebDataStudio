using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.Sqlite;

public sealed class SqliteDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public override string ParameterPrefix => "$";

    // Integer affinity applies to the comparison, so the text a parameter carries is read as the
    // number it is. No cast, which is good: CAST('5' AS integer) is fine, CAST(x AS rowid) is not.
    public override string? RowAddress => "rowid";
    // SQLite keeps a timestamp as ISO text, where a comparison already sorts correctly. Casting it
    // would be worse than useless: CAST('2026-08-23' AS timestamp) has numeric affinity here, and
    // that reads the string as the number 2026.
    public override string TimestampCast => "{0}";

    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";

    // SQLite's own five: everything else is an alias for one of them, and writing INTEGER and TEXT
    // is what makes a column behave the way the file did.
    public override string BooleanType => "INTEGER";
    public override string SmallIntType => "INTEGER";
    public override string IntType => "INTEGER";
    public override string BigIntType => "INTEGER";
    public override string DoubleType => "REAL";
    public override string DateType => "TEXT";
    public override string TimeType => "TEXT";
    public override string TimestampType => "TEXT";
    public override string DecimalType(string duckdbType) => "NUMERIC";

}
