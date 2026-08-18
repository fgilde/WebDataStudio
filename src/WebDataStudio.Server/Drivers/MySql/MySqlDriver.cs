using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.MySql;

// Placeholder: the real implementation lands in this phase's driver task.
public sealed class MySqlDriver : IDbDriver
{
    public DriverInfo Info { get; } = new("mysql", "MySQL / MariaDB", 3306, "Server=localhost;Port=3306;Database=mysql;User ID=root;Password=");
    public DriverCapabilities Caps { get; } = new();
    public SqlDialect Dialect { get; } = new MySqlDialect();

    public Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<SchemaNode>> IntrospectAsync(IDbSession session, SchemaNodeRef? parent, CancellationToken ct) => throw new NotImplementedException();
    public Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct) => throw new NotImplementedException();
    public IAsyncEnumerable<ResultChunk> ExecuteAsync(IDbSession session, ScriptRequest request, CancellationToken ct) => throw new NotImplementedException();
    public Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct) => throw new NotImplementedException();
    public Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope, SchemaNodeRef? target, CancellationToken ct) => throw new NotImplementedException();
}
