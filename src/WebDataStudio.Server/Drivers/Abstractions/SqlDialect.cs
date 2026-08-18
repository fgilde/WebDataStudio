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
