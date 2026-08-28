namespace WebDataStudio.Server.Drivers.Abstractions;

public sealed record ColumnMeta(string Name, string DataType, bool Nullable);

public abstract record ResultChunk(int Statement)
{
    public sealed record Columns(int Statement, IReadOnlyList<ColumnMeta> Items) : ResultChunk(Statement);
    public sealed record Rows(int Statement, IReadOnlyList<object?[]> Items) : ResultChunk(Statement);

    /// Non-SQL engines answer with documents, not rows. The client renders them as a JSON tree and
    /// can flatten them into a table when they happen to be flat.
    public sealed record Documents(int Statement, IReadOnlyList<System.Text.Json.JsonElement> Items)
        : ResultChunk(Statement);
    public sealed record Progress(int Statement, long RowsRead, long ElapsedMs) : ResultChunk(Statement);
    public sealed record Message(int Statement, string Severity, string Text) : ResultChunk(Statement);
    public sealed record End(int Statement, long RowsAffected, long ElapsedMs, bool Truncated) : ResultChunk(Statement);
    public sealed record Error(int Statement, string Text, string? Code, int? Line, int? Column) : ResultChunk(Statement);
}

public sealed record ScriptRequest(
    string Sql,
    int MaxRows,
    int TimeoutSeconds,
    string? Schema = null,
    IReadOnlyDictionary<string, string?>? Parameters = null,
    /// Runs the whole script inside one transaction: it commits when every statement
    /// succeeded and rolls back on the first failure. Off is the engines' own auto-commit.
    bool Transactional = false);

public enum PlanMode { Estimated, Actual }

public sealed record PlanNode(
    string Operation,
    string? Detail,
    double? EstimatedCost,
    double? EstimatedRows,
    double? ActualRows,
    double? ActualMs,
    IReadOnlyList<PlanNode> Children,
    IReadOnlyList<string> Warnings);

public enum AnalyzeScope { Connection, Schema, Table, Query }

/// What a data tab is asking for: one page, in some order, possibly filtered on one column.
///
/// The filter is the studio's own small language — `^starts`, `>10`, `NULL`, a plain word for
/// "contains" — and each driver honours as much of it as its engine can. What it cannot do it says
/// rather than pretending.
public sealed record PageQuery(
    int Offset, int Limit, string? Sort, bool Desc, string? FilterColumn, string? Filter);

/// One page of rows from an engine that built it itself.
public sealed record TabularPage(
    IReadOnlyList<ColumnMeta> Columns,
    IReadOnlyList<object?[]> Rows,
    /// How many there are in total, where the engine can say cheaply. Null is "unknown".
    long? Total,
    /// Whether this grid may write. False carries a reason the person reads instead of a disabled
    /// button with no explanation.
    bool Editable,
    string? Reason,
    /// What the driver could not do with the query it was given — an unsupported filter, a sort a
    /// key space has no order for. Shown, not swallowed.
    string? Note = null);

public sealed record AnalyzeFinding(
    string Category, string Severity, string Title, string Detail, string? Statement);

public sealed record AnalyzeReport(IReadOnlyList<AnalyzeFinding> Findings);
