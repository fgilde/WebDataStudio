using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Import;

/// What "keep this result as a table" would do, before it does it.
public sealed record ResultTablePlan(
    string Schema,
    string Table,
    IReadOnlyList<ImportColumn> Columns,
    /// The `CREATE TABLE` the target engine will run, for reading before it runs.
    string CreateSql,
    /// True when source and target are the same engine, which is when the column types are the
    /// source's own rather than the nearest thing the target has.
    bool ExactTypes);

/// A result becomes a table.
///
/// The half that was missing: copying a *table* somewhere else has been possible for a while, and
/// filling a table that already exists is the import. This is "I have just worked out this join and
/// I will need it again" — the result of any statement, kept as a table, here or in another
/// connection.
///
/// One path, not two. `CREATE TABLE … AS SELECT` would be shorter for the same connection and
/// useless for any other, so the rows travel the way they do for a copy: the statement runs, its
/// columns become a `CREATE TABLE`, and the rows are inserted in batches.
public sealed class ResultTableImport(SessionFactory factory)
{
    /// What this engine calls the nearest thing to a column of that type.
    ///
    /// Only for a copy between two different engines, where "numeric(12,4)" means nothing to the
    /// target. It widens rather than guesses: a column somebody can narrow afterwards beats an
    /// import that quietly rounded.
    public static string TargetType(SqlDialect dialect, string sourceType)
    {
        var type = sourceType.ToLowerInvariant();

        return FilterExpression.KindOf(sourceType) switch
        {
            FilterKind.Boolean => dialect.BooleanType,
            FilterKind.Date => type.Contains("date") && !type.Contains("time")
                ? dialect.DateType
                : dialect.TimestampType,
            FilterKind.Number => type.Contains("int") || type.Contains("serial")
                ? dialect.BigIntType
                : dialect.DoubleType,
            _ => dialect.TextType,
        };
    }

    /// Runs the statement for its shape only — one row is enough to learn the columns — and writes
    /// the plan. Nothing is created here.
    public async Task<ResultTablePlan> PlanAsync(string sourceConnectionId, string sql,
        string targetConnectionId, string schema, string table, CancellationToken ct)
    {
        var (sourceDriver, sourceSession) = await factory.OpenAsync(sourceConnectionId, ct);
        await using (sourceSession)
        {
            var (targetDriver, targetSession) = await factory.OpenAsync(targetConnectionId, ct);
            await using (targetSession)
            {
                var columns = await ColumnsAsync(sourceDriver, sourceSession, sql, ct);
                return Plan(sourceDriver, targetDriver, columns, schema, table);
            }
        }
    }

    /// Creates the table and fills it. The plan is built again from the statement rather than
    /// trusted from the browser: what runs is what the statement says now.
    public async Task<ImportOutcome> RunAsync(string sourceConnectionId, string sql,
        string targetConnectionId, string schema, string table, int maxRows, CancellationToken ct)
    {
        var (sourceDriver, sourceSession) = await factory.OpenAsync(sourceConnectionId, ct);
        await using (sourceSession)
        {
            var (targetDriver, targetSession) = await factory.OpenAsync(targetConnectionId, ct);
            await using (targetSession)
            {
                if (targetSession.Spec.ReadOnly)
                    throw new InvalidOperationException(
                        "this connection is read-only; nothing was created");

                var columns = new List<ColumnMeta>();
                var rows = ReadAsync(sourceDriver, sourceSession,
                    new ScriptRequest(sql, maxRows, 300), columns, ct);

                // The first chunk carries the columns, so the table cannot be created until the
                // first row has been pulled: the same dance the table copy does.
                var enumerator = rows.GetAsyncEnumerator(ct);

                try
                {
                    var hasFirst = await enumerator.MoveNextAsync();
                    var plan = Plan(sourceDriver, targetDriver, columns, schema, table);

                    await using (var create = targetSession.Connection.CreateCommand())
                    {
                        create.CommandText = plan.CreateSql;
                        await create.ExecuteNonQueryAsync(ct);
                    }

                    async IAsyncEnumerable<object?[]> Remaining()
                    {
                        if (hasFirst) yield return enumerator.Current;
                        while (await enumerator.MoveNextAsync()) yield return enumerator.Current;
                    }

                    var qualified = Qualify(targetDriver, schema, table);

                    var result = await new ImportService().ExecuteAsync(targetDriver, targetSession,
                        qualified, plan.Columns.Select(c => c.Name).ToList(), Remaining(), ct);

                    return new ImportOutcome(qualified, result.Inserted, plan.CreateSql);
                }
                finally
                {
                    await enumerator.DisposeAsync();
                }
            }
        }
    }

    private static ResultTablePlan Plan(IDbDriver source, IDbDriver target,
        IReadOnlyList<ColumnMeta> columns, string schema, string table)
    {
        if (columns.Count == 0)
            throw new InvalidOperationException("this statement returns no columns");

        // Two columns called the same thing is ordinary in a result and impossible in a table, and
        // a column with no name at all is what `SELECT count(*)` gives some engines.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = columns.Select((column, index) =>
        {
            var name = column.Name is { Length: > 0 } ? column.Name : $"column{index + 1}";
            var unique = name;
            var suffix = 2;
            while (!seen.Add(unique)) unique = $"{name}_{suffix++}";
            return (Name: unique, column.DataType);
        }).ToList();

        // Same engine, same types: nothing is approximated when nothing has to be.
        var exact = source.Info.Id == target.Info.Id;

        var mapped = named
            .Select(c => new ImportColumn(c.Name, c.DataType,
                exact ? c.DataType : TargetType(target.Dialect, c.DataType)))
            .ToList();

        var writer = Endpoints.DdlEndpoints.WriterFor(target.Info.Id)
            ?? throw new NotSupportedException(
                $"{target.Info.Label} cannot be given a new table by this studio");

        var definition = new TableDefinition(schema, table,
            mapped.Select(column =>
                new ColumnDefinition(column.Name, column.TargetType, true, null, false, null)).ToList(),
            [], [], null);

        var create = string.Join(";\n", writer.CreateTable(definition).Select(s => s.Sql));

        return new ResultTablePlan(schema, table, mapped, create, exact);
    }

    private static string Qualify(IDbDriver driver, string schema, string table) =>
        driver.Caps.MultiSchema && schema is { Length: > 0 } ? $"{schema}.{table}" : table;

    private static async Task<List<ColumnMeta>> ColumnsAsync(IDbDriver driver, IDbSession session,
        string sql, CancellationToken ct)
    {
        var columns = new List<ColumnMeta>();

        // One row: enough to learn the shape, cheap enough to ask before anything is created.
        await foreach (var chunk in driver.ExecuteAsync(session, new ScriptRequest(sql, 1, 300), ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns.Clear();
                    columns.AddRange(c.Items);
                    break;

                case ResultChunk.Error error:
                    throw new InvalidOperationException(error.Text);
            }
        }

        return columns;
    }

    private static async IAsyncEnumerable<object?[]> ReadAsync(IDbDriver driver, IDbSession session,
        ScriptRequest request, List<ColumnMeta> columns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns.Clear();
                    columns.AddRange(c.Items);
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
