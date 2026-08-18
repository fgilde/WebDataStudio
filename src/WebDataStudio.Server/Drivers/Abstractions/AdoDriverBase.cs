using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Drivers.Abstractions;

public sealed class AdoSession(ConnectionSpec spec, DbConnection connection) : IDbSession
{
    public ConnectionSpec Spec { get; } = spec;
    public DbConnection Connection { get; } = connection;
    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}

/// Shared execution machinery for every ADO.NET driver: statement splitting, read-only
/// enforcement, chunked streaming, error mapping.
public abstract class AdoDriverBase : IDbDriver
{
    private const int ChunkSize = 200;

    public abstract DriverInfo Info { get; }
    public abstract DriverCapabilities Caps { get; }
    public abstract SqlDialect Dialect { get; }

    public abstract Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct);
    public abstract Task<IReadOnlyList<SchemaNode>> IntrospectAsync(IDbSession session, SchemaNodeRef? parent, CancellationToken ct);
    public abstract Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct);

    public virtual Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct) =>
        throw new NotSupportedException($"{Info.Label} does not support execution plans");

    public virtual Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope, SchemaNodeRef? target, CancellationToken ct) =>
        Task.FromResult(new AnalyzeReport([]));

    public async IAsyncEnumerable<ResultChunk> ExecuteAsync(
        IDbSession session, ScriptRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var statements = StatementSplitter.Split(request.Sql, Dialect);

        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];

            if (session.Spec.ReadOnly && !Dialect.IsReadOnlyStatement(statement.Text))
            {
                yield return new ResultChunk.Error(index,
                    "this connection is read-only; the statement was not executed", "WDS_READONLY", null, null);
                yield break;
            }

            await foreach (var chunk in RunOneAsync(session, statement.Text, index, request, ct))
                yield return chunk;
        }
    }

    private async IAsyncEnumerable<ResultChunk> RunOneAsync(
        IDbSession session, string sql, int index, ScriptRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = request.TimeoutSeconds;
        AddParameters(command, request.Parameters);

        DbDataReader? reader = null;
        ResultChunk.Error? failure = null;
        try
        {
            reader = await command.ExecuteReaderAsync(ct);
        }
        catch (DbException e)
        {
            // A rejected statement is data for the client, not an exception for the pipeline.
            // C# forbids yielding from a catch block, so the chunk is emitted just below.
            var (line, column) = LocateError(e, sql);
            failure = new ResultChunk.Error(index, e.Message, e.SqlState, line, column);
        }

        if (failure is not null || reader is null)
        {
            await command.DisposeAsync();
            yield return failure ?? new ResultChunk.Error(index, "the driver returned no reader", null, null, null);
            yield break;
        }

        await using (reader)
        await using (command)
        {
            do
            {
                if (reader.FieldCount == 0)
                {
                    yield return new ResultChunk.End(index, reader.RecordsAffected, watch.ElapsedMilliseconds, false);
                    continue;
                }

                var columns = new ColumnMeta[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    columns[i] = new ColumnMeta(reader.GetName(i), reader.GetDataTypeName(i), true);
                yield return new ResultChunk.Columns(index, columns);

                var buffer = new List<object?[]>(ChunkSize);
                long read = 0;
                var truncated = false;

                while (await reader.ReadAsync(ct))
                {
                    if (read >= request.MaxRows) { truncated = true; break; }

                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[i] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i));
                    buffer.Add(row);
                    read++;

                    if (buffer.Count >= ChunkSize)
                    {
                        yield return new ResultChunk.Rows(index, buffer.ToArray());
                        yield return new ResultChunk.Progress(index, read, watch.ElapsedMilliseconds);
                        buffer.Clear();
                    }
                }

                if (buffer.Count > 0) yield return new ResultChunk.Rows(index, buffer.ToArray());
                yield return new ResultChunk.End(index, reader.RecordsAffected, watch.ElapsedMilliseconds, truncated);
            }
            while (await reader.NextResultAsync(ct));
        }
    }

    /// Values that do not survive JSON round-tripping become strings the grid can render.
    protected virtual object? Normalize(object value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime dt => dt.ToString("O"),
        DateTimeOffset dto => dto.ToString("O"),
        TimeSpan ts => ts.ToString(),
        _ => value,
    };

    /// Engines that report an error position override this so Monaco can mark the exact spot.
    protected virtual (int? Line, int? Column) LocateError(DbException exception, string sql) => (null, null);

    private static void AddParameters(DbCommand command, IReadOnlyDictionary<string, string?>? parameters)
    {
        if (parameters is null) return;
        foreach (var (key, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = key;
            parameter.Value = (object?)value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
