using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Editing;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class DataEndpoints
{
    public record ChangeDto(string Kind, Dictionary<string, JsonElement> Key, Dictionary<string, JsonElement> Values);
    public record ChangeRequest(List<ChangeDto> Changes);
    public record ApplyRequest(string Hash);

    public static void MapDataEndpoints(this WebApplication app)
    {
        var defaultLimit = int.TryParse(app.Configuration["WDS_MAX_ROWS"], out var m) ? m : 1000;
        var timeout = int.TryParse(app.Configuration["WDS_QUERY_TIMEOUT_SECONDS"], out var t) ? t : 300;

        app.MapGet("/api/data/{conn}/{objectRef}", async (string conn, string objectRef,
            int? offset, int? limit, string? sort, bool? desc, string? filterColumn, string? filter,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);
                    var identity = RowIdentity.Resolve(detail);

                    var table = ChangeScriptBuilder.Qualify(target, driver.Dialect);
                    var take = Math.Clamp(limit ?? defaultLimit, 1, 100_000);
                    var skip = Math.Max(offset ?? 0, 0);

                    // Filter values are parameterised; only identifiers are interpolated, and those
                    // are checked against the real column list before they go anywhere near SQL.
                    var columnNames = detail.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var where = "";
                    var parameters = new Dictionary<string, object?>();

                    if (filterColumn is { Length: > 0 } && columnNames.Contains(filterColumn) && filter is not null)
                    {
                        where = $" WHERE CAST({driver.Dialect.QuoteIdentifier(filterColumn)} AS " +
                                $"{CharType(driver)}) LIKE {driver.Dialect.ParameterPrefix}f";
                        parameters["f"] = $"%{filter}%";
                    }

                    var order = sort is { Length: > 0 } && columnNames.Contains(sort)
                        ? $" ORDER BY {driver.Dialect.QuoteIdentifier(sort)}{(desc == true ? " DESC" : "")}"
                        : "";

                    var sql = driver.Dialect.Paginate($"SELECT * FROM {table}{where}{order}", skip, take);
                    var request = new ScriptRequest(sql, take, timeout,
                        Parameters: parameters.ToDictionary(p => p.Key, p => (string?)p.Value?.ToString()));

                    var columns = new List<ColumnMeta>();
                    var rows = new List<object?[]>();
                    string? error = null;

                    await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
                    {
                        switch (chunk)
                        {
                            case ResultChunk.Columns c: columns = c.Items.ToList(); break;
                            case ResultChunk.Rows r: rows.AddRange(r.Items); break;
                            case ResultChunk.Error e: error = e.Text; break;
                        }
                    }

                    if (error is not null) return Results.Json(new { message = error }, statusCode: 502);

                    return Results.Ok(new
                    {
                        columns,
                        rows,
                        editable = identity.Editable && !session.Spec.ReadOnly,
                        keyColumns = identity.KeyColumns,
                        reason = session.Spec.ReadOnly ? "this connection is read-only" : identity.Reason,
                        totalEstimate = detail.RowCount,
                        offset = skip,
                        limit = take,
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/data/{conn}/{objectRef}/preview-changes", async (string conn, string objectRef,
            ChangeRequest body, SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);
                    var identity = RowIdentity.Resolve(detail);

                    if (!identity.Editable)
                        return Results.BadRequest(new { message = identity.Reason });

                    var changeSet = ToChangeSet(conn, target.ToString(), body);
                    if (changeSet.Changes.Count == 0)
                        return Results.BadRequest(new { message = "there is nothing to apply" });

                    var script = ChangeScriptBuilder.Build(changeSet, detail, driver.Dialect);
                    var hash = changeSet.Hash();

                    // The built script is cached, so apply executes exactly what was approved
                    // rather than rebuilding it from a request that could have changed.
                    cache.Set($"changes:{hash}", (target, script), TimeSpan.FromMinutes(10));

                    return Results.Ok(new
                    {
                        hash,
                        script = script.Text,
                        statementCount = script.Statements.Count,
                        destructive = script.Statements.Any(s => s.Destructive),
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/data/{conn}/{objectRef}/apply-changes", async (string conn, string objectRef,
            ApplyRequest body, SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            if (cache.Get($"changes:{body.Hash}") is not ValueTuple<SchemaNodeRef, ChangeScript> cached)
                return Results.Json(
                    new { message = "the preview expired or the data changed; preview again before applying" },
                    statusCode: StatusCodes.Status409Conflict);

            var (_, script) = cached;

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Spec.ReadOnly)
                        return Results.Json(new { message = "this connection is read-only" },
                            statusCode: StatusCodes.Status403Forbidden);

                    var applied = 0;
                    DbTransaction? transaction = driver.Caps.Transactions
                        ? await session.Connection.BeginTransactionAsync(ct)
                        : null;

                    try
                    {
                        foreach (var statement in script.Statements)
                        {
                            await using var command = session.Connection.CreateCommand();
                            command.CommandText = statement.Sql;
                            command.Transaction = transaction;

                            foreach (var (name, value) in statement.Parameters)
                            {
                                var parameter = command.CreateParameter();
                                parameter.ParameterName = name;
                                parameter.Value = ChangeScriptBuilder.Normalize(value) ?? DBNull.Value;
                                command.Parameters.Add(parameter);
                            }

                            await command.ExecuteNonQueryAsync(ct);
                            applied++;
                        }

                        if (transaction is not null) await transaction.CommitAsync(ct);
                        cache.Remove($"changes:{body.Hash}");
                        return Results.Ok(new { applied, failedAt = (int?)null, error = (string?)null });
                    }
                    catch (DbException e)
                    {
                        if (transaction is not null) await transaction.RollbackAsync(ct);

                        return Results.Json(new
                        {
                            applied = transaction is null ? applied : 0,
                            failedAt = applied,
                            error = e.Message,
                            rolledBack = transaction is not null,
                        }, statusCode: 400);
                    }
                    finally
                    {
                        if (transaction is not null) await transaction.DisposeAsync();
                    }
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/data/{conn}/{objectRef}/lookup", async (string conn, string objectRef,
            string column, string? search, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);

                    if (!detail.Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                        return Results.BadRequest(new { message = $"no column '{column}'" });

                    // A second, text-like column makes the dropdown readable; without one the key
                    // is shown on its own.
                    var label = detail.Columns
                        .FirstOrDefault(c => !c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)
                                             && c.DataType.Contains("char", StringComparison.OrdinalIgnoreCase)
                                             || c.DataType.Contains("text", StringComparison.OrdinalIgnoreCase));

                    var key = driver.Dialect.QuoteIdentifier(column);
                    var labelExpression = label is null ? key : driver.Dialect.QuoteIdentifier(label.Name);
                    var table = ChangeScriptBuilder.Qualify(target, driver.Dialect);

                    var where = "";
                    var parameters = new Dictionary<string, string?>();
                    if (search is { Length: > 0 })
                    {
                        where = $" WHERE CAST({labelExpression} AS {CharType(driver)}) " +
                                $"LIKE {driver.Dialect.ParameterPrefix}s";
                        parameters["s"] = $"%{search}%";
                    }

                    var sql = driver.Dialect.Paginate(
                        $"SELECT {key}, {labelExpression} FROM {table}{where}", 0, 50);

                    var items = new List<object?[]>();
                    await foreach (var chunk in driver.ExecuteAsync(session,
                        new ScriptRequest(sql, 50, timeout, Parameters: parameters), ct))
                        if (chunk is ResultChunk.Rows rows) items.AddRange(rows.Items);

                    return Results.Ok(items.Select(r => new { value = r[0], label = r.Length > 1 ? r[1] : r[0] }));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    /// The type name every engine accepts in a CAST to text.
    private static string CharType(IDbDriver driver) => driver.Info.Id switch
    {
        "sqlserver" => "NVARCHAR(MAX)",
        "mysql" => "CHAR",
        "oracle" => "VARCHAR2(4000)",
        _ => "TEXT",
    };

    private static ChangeSet ToChangeSet(string connectionId, string objectRef, ChangeRequest body) =>
        new(connectionId, objectRef, body.Changes.Select(c => new RowChange(
            c.Kind,
            c.Key.ToDictionary(k => k.Key, k => (object?)k.Value),
            c.Values.ToDictionary(k => k.Key, k => (object?)k.Value))).ToList());
}
