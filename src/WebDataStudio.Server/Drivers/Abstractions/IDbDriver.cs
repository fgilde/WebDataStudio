using System.Data.Common;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.Abstractions;

/// An open connection to one database. Disposing it returns the underlying connection.
public interface IDbSession : IAsyncDisposable
{
    ConnectionSpec Spec { get; }
    DbConnection Connection { get; }
}

// The spec's `IDdlWriter Ddl` property is deliberately absent: nothing here writes DDL. P3 adds it
// with a CreateTable-only writer for the SQL schema exporter, P6 grows it into the full interface.
public interface IDbDriver
{
    DriverInfo Info { get; }
    DriverCapabilities Caps { get; }
    SqlDialect Dialect { get; }

    Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct);

    /// One level of the object tree. `parent` is null for the root of the connection.
    Task<IReadOnlyList<SchemaNode>> IntrospectAsync(IDbSession session, SchemaNodeRef? parent, CancellationToken ct);

    Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct);

    IAsyncEnumerable<ResultChunk> ExecuteAsync(IDbSession session, ScriptRequest request, CancellationToken ct);

    Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct);

    Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope, SchemaNodeRef? target, CancellationToken ct);
}
