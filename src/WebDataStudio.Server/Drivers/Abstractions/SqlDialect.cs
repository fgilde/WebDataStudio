namespace WebDataStudio.Server.Drivers.Abstractions;

/// Everything the formatter, the DDL writer and the paging code need to know about syntax.
public abstract class SqlDialect
{
    public abstract string QuoteIdentifier(string name);

    /// What this engine calls "where the row physically is", for a table that has no key at all.
    /// Null where there is no usable answer: MySQL keeps its row id to itself, and SQL Server's
    /// %%physloc%% is undocumented. See RowIdentity, which only reaches for this as a last resort.
    public virtual string? RowAddress => null;

    /// The predicate that finds that row again. The address is not text, and most engines say so.
    public virtual string RowAddressPredicate(string parameter) => $"{RowAddress} = {parameter}";
    public abstract string ParameterPrefix { get; }

    /// True when this engine separates batches with a standalone GO line (SQL Server).
    public virtual bool UsesGoBatchSeparator => false;

    /// Wraps a SELECT so the server returns at most `limit` rows starting at `offset`.
    public abstract string Paginate(string sql, int offset, int limit);

    public string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    /// The type name this engine accepts in a CAST to text.
    public virtual string TextType => "TEXT";

    /// How a parameter that arrived as text is read as a number. Parameters travel as strings
    /// (see ScriptRequest), and PostgreSQL will not compare `numeric > $1` when `$1` is text — it
    /// says so rather than guessing, which is the right call and has to be answered here.
    /// <c>{0}</c> is the parameter.
    public virtual string NumberCast => "CAST({0} AS numeric)";

    // --- the type names a new table is written with ------------------------------------------
    // A file's columns arrive as DuckDB's types and have to be written as this engine's. Named
    // properties rather than a mapping table: each engine then says its own spelling once, and a new
    // engine that forgets one gets the default rather than a wrong column.

    public virtual string BooleanType => "BOOLEAN";
    public virtual string SmallIntType => "SMALLINT";
    public virtual string IntType => "INTEGER";
    public virtual string BigIntType => "BIGINT";
    public virtual string DoubleType => "DOUBLE PRECISION";
    public virtual string DateType => "DATE";
    public virtual string TimeType => "TIME";
    public virtual string TimestampType => "TIMESTAMP";

    /// `DECIMAL(18,2)` as this engine spells it. The precision comes from the file, so it travels
    /// through rather than being flattened to a default.
    public virtual string DecimalType(string duckdbType) => duckdbType;

    /// One path inside a JSON column, as text. `column` is already quoted; `path` is dotted, with
    /// `[]` where an array was folded into one entry.
    ///
    /// The default is the SQL/JSON path spelling that PostgreSQL, SQLite and DuckDB all accept;
    /// MySQL, SQL Server, Oracle and ClickHouse say it their own way and override this.
    public virtual string JsonPath(string column, string path) =>
        $"json_extract({column}, '{JsonPathLiteral(path)}')";

    /// `a.b[].c` as the `$.a.b[*].c` that the SQL/JSON functions want, with quotes escaped.
    protected static string JsonPathLiteral(string path)
    {
        var steps = path.Replace("[]", "[*]").Split('.', StringSplitOptions.RemoveEmptyEntries);
        return "$." + string.Join(".", steps).Replace("'", "''");
    }

    /// The same for a timestamp. Written as an expression rather than a type name because Oracle
    /// needs a format string and everybody else does not.
    public virtual string TimestampCast => "CAST({0} AS timestamp)";

    /// Classifies a statement so a read-only connection can refuse anything that writes.
    public virtual bool IsReadOnlyStatement(string sql)
    {
        var head = sql.TrimStart();

        // Strip leading comments so "-- comment\nDELETE ..." is not mistaken for a read.
        while (head.StartsWith("--") || head.StartsWith("/*"))
        {
            if (head.StartsWith("--"))
            {
                var newline = head.IndexOf('\n');
                if (newline < 0) return false;
                head = head[(newline + 1)..].TrimStart();
            }
            else
            {
                var close = head.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0) return false;
                head = head[(close + 2)..].TrimStart();
            }
        }

        return head.StartsWith("select", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("with", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("show", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("explain", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("describe", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("pragma", StringComparison.OrdinalIgnoreCase);
    }
}
