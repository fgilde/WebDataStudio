using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Export;

public sealed record ExportOptions(
    string Delimiter, string Encoding, bool Header, string NullText,
    string DateFormat, bool QuoteAll, string? TableName)
{
    /// Set by the endpoint from the target connection: the SQL exporters quote identifiers and
    /// render literals with it, which is what makes a cross-engine export correct.
    public SqlDialect? Dialect { get; init; }

    public static ExportOptions Default { get; } = new(",", "utf-8", true, "", "O", false, null);
}
