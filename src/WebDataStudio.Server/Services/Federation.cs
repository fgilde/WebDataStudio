using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One query against one connection, staged under `Alias`.
public sealed record FederationSource(string ConnectionId, string Sql, string Alias);

public sealed record FederationRequest(
    IReadOnlyList<FederationSource> Sources, string Sql, int? MaxRowsPerSource);

/// Something the caller can fix: a bad alias, too much data, SQL DuckDB will not take.
public sealed class FederationException(string message) : Exception(message);

/// A join across connections. Each source query runs where it lives, its rows are staged in an
/// in-memory DuckDB table named by its alias, and the federated SQL runs there.
///
/// Staging is the honest part of this: the studio does not pretend two databases are one, it says
/// how much it copied and refuses when that would be too much.
public sealed partial class Federation(SessionFactory factory, MaskPolicyStore policies)
{
    /// Above this, staging stops being a query and starts being an import.
    public const int DefaultMaxRowsPerSource = 100_000;

    /// Rows per INSERT while staging.
    // ponytail: multi-row INSERT rather than DuckDB's appender - fast enough for the row cap above,
    // and it needs no type-by-type binding. Move to the appender if the cap ever grows.
    private const int BatchSize = 500;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex AliasShape();

    public async IAsyncEnumerable<ResultChunk> RunAsync(
        FederationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Validate(request);

        await using var duck = new DuckDBConnection("DataSource=:memory:");
        await duck.OpenAsync(ct);

        foreach (var source in request.Sources)
            await StageAsync(duck, source, request.MaxRowsPerSource ?? DefaultMaxRowsPerSource, ct);

        await using var command = duck.CreateCommand();
        command.CommandText = request.Sql;

        DbDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(ct);
        }
        catch (DuckDBException e)
        {
            // An unknown table here is a typo in the federated SQL or a missing source, both of
            // which the caller fixes; naming the aliases that do exist saves them a guess.
            throw new FederationException(
                $"{e.Message} — staged sources: {string.Join(", ", request.Sources.Select(s => s.Alias))}");
        }

        await using (reader)
        {
            var columns = new List<ColumnMeta>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(new ColumnMeta(reader.GetName(i), reader.GetDataTypeName(i), true));

            yield return new ResultChunk.Columns(0, columns);

            var rows = new List<object?[]>();
            var total = 0;

            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);

                rows.Add(row);
                total++;

                if (rows.Count < BatchSize) continue;
                yield return new ResultChunk.Rows(0, rows);
                rows = [];
            }

            if (rows.Count > 0) yield return new ResultChunk.Rows(0, rows);
            yield return new ResultChunk.End(0, total, 0, false);
        }
    }

    /// What the run would stage, without pulling the data: the table DuckDB would create per
    /// source, so a mistake in an alias or a source query shows up before anything is copied.
    public async Task<IReadOnlyList<(string Alias, string Ddl)>> PreviewAsync(
        FederationRequest request, CancellationToken ct)
    {
        Validate(request);
        var plan = new List<(string, string)>();

        foreach (var source in request.Sources)
        {
            var columns = await ColumnsOfAsync(source, ct);
            plan.Add((source.Alias, CreateTable(source.Alias, columns)));
        }

        return plan;
    }

    private void Validate(FederationRequest request)
    {
        if (request.Sources.Count == 0)
            throw new FederationException("a federated query needs at least one source");

        if (string.IsNullOrWhiteSpace(request.Sql))
            throw new FederationException("a federated query needs SQL to run over its sources");

        foreach (var source in request.Sources)
        {
            if (!AliasShape().IsMatch(source.Alias))
                throw new FederationException(
                    $"'{source.Alias}' cannot be a table name; use letters, digits and underscores");

            if (string.IsNullOrWhiteSpace(source.Sql))
                throw new FederationException($"source '{source.Alias}' has no query");
        }

        var duplicate = request.Sources
            .GroupBy(s => s.Alias, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            throw new FederationException($"two sources are both called '{duplicate.Key}'");
    }

    /// Runs the source query for its shape only, with a single row, so a preview costs nothing.
    private async Task<IReadOnlyList<ColumnMeta>> ColumnsOfAsync(
        FederationSource source, CancellationToken ct)
    {
        var (driver, session) = await factory.OpenAsync(source.ConnectionId, ct);
        await using (session)
        {
            await foreach (var chunk in driver.ExecuteAsync(session, new ScriptRequest(source.Sql, 1, 60), ct))
                switch (chunk)
                {
                    case ResultChunk.Columns columns:
                        return columns.Items;
                    case ResultChunk.Error error:
                        throw new FederationException($"source '{source.Alias}': {error.Text}");
                }
        }

        throw new FederationException($"source '{source.Alias}' returned no result to stage");
    }

    private async Task StageAsync(
        DuckDBConnection duck, FederationSource source, int maxRows, CancellationToken ct)
    {
        var (driver, session) = await factory.OpenAsync(source.ConnectionId, ct);
        // Secrets stay masked on their way through: a federated query is another way into the same
        // data, and DuckDB would happily carry them out of the building.
        var policy = policies.For(source.ConnectionId);

        await using (session)
        {
            IReadOnlyList<ColumnMeta>? columns = null;
            var staged = 0;
            var batch = new List<object?[]>();

            // The cap plus one: reading one row past it is how the refusal knows it is right.
            var request = new ScriptRequest(source.Sql, maxRows + 1, 300);

            await foreach (var chunk in Masking.Stream(driver.ExecuteAsync(session, request, ct), policy, ct))
            {
                switch (chunk)
                {
                    case ResultChunk.Columns c:
                        columns = c.Items;
                        await ExecuteAsync(duck, CreateTable(source.Alias, c.Items), ct);
                        break;

                    case ResultChunk.Error e:
                        throw new FederationException($"source '{source.Alias}': {e.Text}");

                    case ResultChunk.Rows r when columns is not null:
                        foreach (var row in r.Items)
                        {
                            staged++;
                            if (staged > maxRows)
                                throw new FederationException(
                                    $"source '{source.Alias}' returned more than {maxRows} rows; " +
                                    "narrow its query or raise the limit for this run");

                            batch.Add(row);
                            if (batch.Count < BatchSize) continue;
                            await InsertAsync(duck, source.Alias, columns, batch, ct);
                            batch.Clear();
                        }
                        break;
                }
            }

            if (columns is null)
                throw new FederationException($"source '{source.Alias}' returned no result to stage");

            if (batch.Count > 0) await InsertAsync(duck, source.Alias, columns, batch, ct);
        }
    }

    private static string CreateTable(string alias, IReadOnlyList<ColumnMeta> columns)
    {
        var definitions = columns.Select((c, i) => $"{Quote(NameOf(c, i))} {DuckType(c.DataType)}");
        return $"CREATE OR REPLACE TABLE {Quote(alias)} ({string.Join(", ", definitions)})";
    }

    private static async Task InsertAsync(DuckDBConnection duck, string alias,
        IReadOnlyList<ColumnMeta> columns, List<object?[]> rows, CancellationToken ct)
    {
        var sql = new StringBuilder($"INSERT INTO {Quote(alias)} VALUES ");
        await using var command = duck.CreateCommand();

        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0) sql.Append(", ");
            sql.Append('(');

            for (var c = 0; c < columns.Count; c++)
            {
                if (c > 0) sql.Append(", ");
                var name = $"p{r}_{c}";
                sql.Append('$').Append(name);

                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = Value(r < rows.Count && c < rows[r].Length ? rows[r][c] : null);
                command.Parameters.Add(parameter);
            }

            sql.Append(')');
        }

        command.CommandText = sql.ToString();
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(DuckDBConnection duck, string sql, CancellationToken ct)
    {
        await using var command = duck.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// Everything DuckDB cannot hold natively is staged as text: a federated join needs the values
    /// to compare, not the source's exact type system.
    private static object Value(object? value) => value switch
    {
        null => DBNull.Value,
        bool or byte or short or int or long or float or double or decimal or string
            or DateTime or Guid => value,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value.ToString() ?? "",
    };

    private static string NameOf(ColumnMeta column, int index) =>
        string.IsNullOrWhiteSpace(column.Name) ? $"column{index}" : column.Name;

    /// A deliberately small mapping: numbers stay numbers so a join and a SUM behave, dates stay
    /// dates so ordering does, and everything else is text.
    private static string DuckType(string dataType)
    {
        var type = dataType.ToLowerInvariant();

        if (type.Contains("bool") || type == "bit") return "BOOLEAN";
        if (type.Contains("timestamp") || type.Contains("datetime")) return "TIMESTAMP";
        if (type.Contains("date")) return "DATE";
        if (type.Contains("time")) return "VARCHAR";
        if (type.Contains("uuid") || type.Contains("guid")) return "UUID";
        if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money")
            || type.Contains("double") || type.Contains("real") || type.Contains("float"))
            return "DOUBLE";
        if (type.Contains("int") || type.Contains("serial") || type.Contains("long")) return "BIGINT";

        return "VARCHAR";
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
