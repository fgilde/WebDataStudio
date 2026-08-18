using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Import;

public sealed record ImportResult(int Inserted, int Failed, IReadOnlyList<string> Errors);

public sealed class ImportService
{
    // One transaction per batch: a bad row costs its batch, not the whole file, and the caller
    // still learns exactly which rows failed.
    private const int BatchSize = 500;
    private const int MaxReportedErrors = 50;

    public async Task<ImportResult> ExecuteAsync(IDbDriver driver, IDbSession session, string table,
        IReadOnlyList<string> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct)
    {
        if (session.Spec.ReadOnly)
            throw new InvalidOperationException("this connection is read-only; nothing was imported");
        if (columns.Count == 0)
            throw new InvalidOperationException("no target columns were mapped");

        var dialect = driver.Dialect;
        var quotedTable = QualifiedName(dialect, table);
        var columnList = string.Join(", ", columns.Select(dialect.QuoteIdentifier));
        var parameters = string.Join(", ", columns.Select((_, i) => $"{dialect.ParameterPrefix}p{i}"));
        var sql = $"INSERT INTO {quotedTable} ({columnList}) VALUES ({parameters})";

        var inserted = 0;
        var failed = 0;
        var errors = new List<string>();
        var rowNumber = 0;
        var batch = new List<object?[]>(BatchSize);

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;

            var start = rowNumber - batch.Count + 1;
            var (ok, bad) = await InsertBatchAsync(session, sql, columns.Count, batch, start, errors, ct);
            inserted += ok;
            failed += bad;
            batch.Clear();
        }

        await foreach (var row in rows.WithCancellation(ct))
        {
            rowNumber++;
            batch.Add(row);
            if (batch.Count >= BatchSize) await FlushAsync();
        }
        await FlushAsync();

        return new ImportResult(inserted, failed, errors);
    }

    private async Task<(int Ok, int Failed)> InsertBatchAsync(IDbSession session, string sql, int width,
        IReadOnlyList<object?[]> batch, int firstRowNumber, List<string> errors, CancellationToken ct)
    {
        await using var transaction = await session.Connection.BeginTransactionAsync(ct);

        var ok = 0;
        var failedRows = new List<(int Number, object?[] Row, string Message)>();

        for (var i = 0; i < batch.Count; i++)
        {
            try
            {
                await ExecuteRowAsync(session, transaction, sql, width, batch[i], ct);
                ok++;
            }
            catch (DbException e)
            {
                failedRows.Add((firstRowNumber + i, batch[i], e.Message));
                break; // the transaction is poisoned; retry the good rows below
            }
        }

        if (failedRows.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return (ok, 0);
        }

        // A row failed mid-batch. Roll back and replay row by row so one bad row does not take its
        // neighbours with it — the whole point of reporting per-row errors.
        await transaction.RollbackAsync(ct);
        return await ReplayIndividuallyAsync(session, sql, width, batch, firstRowNumber, errors, ct);
    }

    private async Task<(int Ok, int Failed)> ReplayIndividuallyAsync(IDbSession session, string sql, int width,
        IReadOnlyList<object?[]> batch, int firstRowNumber, List<string> errors, CancellationToken ct)
    {
        var ok = 0;
        var failed = 0;

        for (var i = 0; i < batch.Count; i++)
        {
            try
            {
                await ExecuteRowAsync(session, null, sql, width, batch[i], ct);
                ok++;
            }
            catch (DbException e)
            {
                failed++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add($"row {firstRowNumber + i}: {e.Message}");
            }
        }

        return (ok, failed);
    }

    private static async Task ExecuteRowAsync(IDbSession session, DbTransaction? transaction, string sql,
        int width, object?[] row, CancellationToken ct)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        for (var c = 0; c < width; c++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{c}";
            parameter.Value = (c < row.Length ? row[c] : null) ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    /// Accepts "schema.table" or a bare name and quotes each part on its own.
    internal static string QualifiedName(SqlDialect dialect, string table) =>
        string.Join(".", table.Split('.').Select(dialect.QuoteIdentifier));
}

/// Copies a table from one connection to another, including across engines.
public sealed class TableCopyService(SessionFactory factory)
{
    public async Task<ImportResult> CopyAsync(string sourceConnectionId, string sourceRef,
        string targetConnectionId, string targetTable, int maxRows, CancellationToken ct)
    {
        var (sourceDriver, sourceSession) = await factory.OpenAsync(sourceConnectionId, ct);
        await using (sourceSession)
        {
            var (targetDriver, targetSession) = await factory.OpenAsync(targetConnectionId, ct);
            await using (targetSession)
            {
                var target = SchemaNodeRef.Parse(sourceRef);
                var qualified = sourceDriver.Caps.MultiSchema && target.Path.Count > 1
                    ? $"{sourceDriver.Dialect.QuoteIdentifier(target.Path[0])}.{sourceDriver.Dialect.QuoteIdentifier(target.Name)}"
                    : sourceDriver.Dialect.QuoteIdentifier(target.Name);

                var request = new ScriptRequest($"SELECT * FROM {qualified}", maxRows, 300);

                var columns = new List<string>();
                var rows = ReadRowsAsync(sourceDriver, sourceSession, request, columns, ct);

                // The first chunk carries the column names; the reader below fills `columns` before
                // the first row is yielded, so the insert statement is known by then.
                var enumerator = rows.GetAsyncEnumerator(ct);
                var hasFirst = await enumerator.MoveNextAsync();

                async IAsyncEnumerable<object?[]> Remaining()
                {
                    if (hasFirst) yield return enumerator.Current;
                    while (await enumerator.MoveNextAsync()) yield return enumerator.Current;
                }

                try
                {
                    return await new ImportService().ExecuteAsync(
                        targetDriver, targetSession, targetTable, columns, Remaining(), ct);
                }
                finally
                {
                    await enumerator.DisposeAsync();
                }
            }
        }
    }

    private static async IAsyncEnumerable<object?[]> ReadRowsAsync(IDbDriver driver, IDbSession session,
        ScriptRequest request, List<string> columns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns.Clear();
                    columns.AddRange(c.Items.Select(x => x.Name));
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items) yield return row;
                    break;

                case ResultChunk.Error error:
                    throw new InvalidOperationException(error.Text);
            }
        }
    }
}
