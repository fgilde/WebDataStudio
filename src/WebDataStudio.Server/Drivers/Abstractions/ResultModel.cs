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
    IReadOnlyDictionary<string, string?>? Parameters = null);

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

public sealed record AnalyzeFinding(
    string Category, string Severity, string Title, string Detail, string? Statement);

public sealed record AnalyzeReport(IReadOnlyList<AnalyzeFinding> Findings);
