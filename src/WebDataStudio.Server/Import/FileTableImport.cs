using System.Data.Common;
using DuckDB.NET.Data;
using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.Storage;

namespace WebDataStudio.Server.Import;

/// One column of a file, as the file has it and as the target will have it.
public sealed record ImportColumn(string Name, string SourceType, string TargetType);

/// What an import would do, before it does it.
public sealed record ImportPlan(
    string Schema,
    string Table,
    IReadOnlyList<ImportColumn> Columns,
    /// The `CREATE TABLE` the target engine will run, for reading before it runs.
    string CreateSql,
    /// How many rows the file has, where the reader can say so without reading it all.
    long? Rows,
    IReadOnlyList<IReadOnlyList<string?>> Preview);

public sealed record ImportOutcome(string Table, int Rows, string CreateSql);

/// A file becomes a table.
///
/// The existing import fills a table that already exists, with the columns mapped by hand — which is
/// the right tool when the table is the point. This is the other half: a CSV or a Parquet somebody
/// was sent, or an object in a bucket, that should simply *be* a table in the database.
///
/// The reading is DuckDB's, which the studio already carries: it infers the types of a CSV better
/// than a hand-rolled sniffer, reads Parquet and JSON, and takes an `s3://` URI as readily as a path.
/// The writing is the target engine's own DDL writer and the ordinary batched insert.
public sealed class FileTableImport(ImportService inserts)
{
    /// Enough rows to show what is coming without reading a large file twice.
    private const int PreviewRows = 10;

    /// What DuckDB says a column is, mapped to what this engine calls it. Anything unrecognised
    /// becomes text: a column somebody can cast is better than an import that refuses.
    public static string TargetType(SqlDialect dialect, string duckdbType)
    {
        var type = duckdbType.ToUpperInvariant();

        if (type.StartsWith("DECIMAL", StringComparison.Ordinal)) return dialect.DecimalType(type);

        return type switch
        {
            "BOOLEAN" => dialect.BooleanType,
            "TINYINT" or "SMALLINT" or "UTINYINT" or "USMALLINT" => dialect.SmallIntType,
            "INTEGER" or "UINTEGER" => dialect.IntType,
            "BIGINT" or "UBIGINT" or "HUGEINT" => dialect.BigIntType,
            "FLOAT" or "REAL" => dialect.DoubleType,
            "DOUBLE" => dialect.DoubleType,
            "DATE" => dialect.DateType,
            "TIME" => dialect.TimeType,
            "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP_MS" or "TIMESTAMP_S"
                or "TIMESTAMP_NS" => dialect.TimestampType,
            "UUID" => dialect.TextType,
            "BLOB" => dialect.TextType,
            _ => dialect.TextType,
        };
    }

    /// Reads the file's shape and writes the plan. Nothing is created here.
    public async Task<ImportPlan> PlanAsync(IDbDriver target, string schema, string table,
        FileSource source, CancellationToken ct)
    {
        await using var duck = await OpenReaderAsync(source, ct);
        var from = source.FromClause();

        var columns = new List<ImportColumn>();

        await using (var command = duck.CreateCommand())
        {
            command.CommandText = $"DESCRIBE SELECT * FROM {from}";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var sourceType = reader.GetString(1);

                columns.Add(new ImportColumn(name, sourceType, TargetType(target.Dialect, sourceType)));
            }
        }

        if (columns.Count == 0)
            throw new InvalidOperationException("this file has no columns to import");

        var preview = new List<IReadOnlyList<string?>>();

        await using (var command = duck.CreateCommand())
        {
            command.CommandText = $"SELECT * FROM {from} LIMIT {PreviewRows}";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                preview.Add(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index).ToString())
                    .ToList());
        }

        long? rows = null;

        // A Parquet knows its own row count; for a CSV, counting means reading it, and the plan is
        // allowed not to know.
        if (source.CanCountCheaply)
            await using (var command = duck.CreateCommand())
            {
                command.CommandText = $"SELECT count(*) FROM {from}";
                var value = await command.ExecuteScalarAsync(ct);
                rows = value is null or DBNull ? null : Convert.ToInt64(value);
            }

        var definition = new TableDefinition(schema, table,
            columns.Select(column =>
                new ColumnDefinition(column.Name, column.TargetType, true, null, false, null)).ToList(),
            [], [], null);

        var writer = Endpoints.DdlEndpoints.WriterFor(target.Info.Id)
            ?? throw new NotSupportedException(
                $"{target.Info.Label} cannot be given a new table by this studio");

        var create = string.Join(";\n", writer.CreateTable(definition).Select(statement => statement.Sql));

        return new ImportPlan(schema, table, columns, create, rows, preview);
    }

    /// Creates the table and loads the file into it. The plan is built again rather than trusted from
    /// the client: what runs is what the file says now.
    public async Task<ImportOutcome> RunAsync(IDbDriver target, IDbSession session, string schema,
        string table, FileSource source, CancellationToken ct)
    {
        if (session.Spec.ReadOnly)
            throw new InvalidOperationException("this connection is read-only; nothing was imported");

        var plan = await PlanAsync(target, schema, table, source, ct);

        await using (var create = session.Connection.CreateCommand())
        {
            create.CommandText = plan.CreateSql;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var duck = await OpenReaderAsync(source, ct);

        var result = await inserts.ExecuteAsync(target, session,
            schema.Length == 0 ? table : $"{schema}.{table}",
            plan.Columns.Select(column => column.Name).ToList(),
            ReadAsync(duck, source.FromClause(), plan.Columns.Count, ct), ct);

        return new ImportOutcome(
            schema.Length == 0 ? table : $"{schema}.{table}", result.Inserted, plan.CreateSql);
    }

    private static async IAsyncEnumerable<object?[]> ReadAsync(DuckDBConnection duck, string from,
        int columns, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var command = duck.CreateCommand();
        command.CommandText = $"SELECT * FROM {from}";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[columns];
            for (var index = 0; index < columns; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);

            yield return row;
        }
    }

    /// A DuckDB that can read this source: nothing to set up for a local file, the storage
    /// extensions and a secret for one in a bucket.
    private static async Task<DuckDBConnection> OpenReaderAsync(FileSource source, CancellationToken ct)
    {
        var duck = new DuckDBConnection("Data Source=:memory:");
        await duck.OpenAsync(ct);

        try
        {
            foreach (var statement in source.Preamble())
            {
                await using var command = duck.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        catch
        {
            await duck.DisposeAsync();
            throw;
        }

        return duck;
    }
}

/// Where the rows come from: a file on this machine, or an object in a bucket.
public abstract record FileSource
{
    /// What DuckDB selects from.
    public abstract string FromClause();

    /// What has to run before it can.
    public virtual IReadOnlyList<string> Preamble() => [];

    /// Whether counting the rows is cheap — a Parquet footer says so, a CSV does not.
    public virtual bool CanCountCheaply => false;
}

/// A file that was uploaded, staged in the studio's temp directory.
public sealed record LocalFileSource(string Path) : FileSource
{
    public override string FromClause() =>
        StorageReader.Call(Path.Replace('\\', '/'))
        ?? throw new NotSupportedException(
            "nothing here reads that file; CSV, TSV, JSON and Parquet are the formats it knows");

    public override bool CanCountCheaply =>
        Path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase);
}

/// An object in a bucket, read where it is rather than downloaded first.
public sealed record StorageFileSource(string Uri, IReadOnlyList<string> Setup) : FileSource
{
    public override string FromClause() =>
        StorageReader.Call(Uri)
        ?? throw new NotSupportedException(
            "nothing here reads that object; CSV, TSV, JSON and Parquet are the formats it knows");

    public override IReadOnlyList<string> Preamble() => Setup;

    public override bool CanCountCheaply =>
        Uri.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase);
}
