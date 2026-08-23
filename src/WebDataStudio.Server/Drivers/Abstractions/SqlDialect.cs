namespace WebDataStudio.Server.Drivers.Abstractions;

/// Everything the formatter, the DDL writer and the paging code need to know about syntax.
public abstract class SqlDialect
{
    public abstract string QuoteIdentifier(string name);
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
