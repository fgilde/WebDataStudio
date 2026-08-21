using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Editing;
using WebDataStudio.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace WebDataStudio.Server.Endpoints;

public static class DataEndpoints
{
    public record MaskPolicyRequest(bool? MaskByDefault, string[]? Extra, string[]? Never);

    public record ChangeDto(string Kind, Dictionary<string, JsonElement> Key, Dictionary<string, JsonElement> Values);
    public record ChangeRequest(List<ChangeDto> Changes);
    public record ApplyRequest(string Hash);

    /// What a preview leaves behind for its apply: the exact script that was approved, plus what
    /// the inverse needs - the change set itself and the columns that address a row.
    private sealed record Prepared(
        SchemaNodeRef Target, ChangeScript Script, ChangeSet Changes, IReadOnlyList<string> KeyColumns);

    public static void MapDataEndpoints(this WebApplication app)
    {
        var defaultLimit = int.TryParse(app.Configuration["WDS_MAX_ROWS"], out var m) ? m : 1000;
        var timeout = int.TryParse(app.Configuration["WDS_QUERY_TIMEOUT_SECONDS"], out var t) ? t : 300;

        // The object reference travels in the query string, not the path: it contains a slash
        // ("Table:dbo/AbpUsers"), and the reverse proxy in front of a deployed studio — Envoy on
        // Azure Container Apps, and most others — decodes %2F back to a real slash before routing.
        // The route then no longer matches and every object lookup answered 404 in the cloud while
        // working on a machine with nothing in front of it.
        app.MapGet("/api/data/{conn}", async (string conn, [FromQuery(Name = "ref")] string objectRef,
            int? offset, int? limit, string? sort, bool? desc, string? filterColumn, string? filter,
            bool? reveal, SessionFactory factory, MaskPolicyStore policies, CancellationToken ct) =>
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

                    // Masked on the server, not in the browser: a value that never leaves here
                    // cannot be read out of a network tab or a screenshot of the dev tools.
                    // Revealing is a deliberate, separate request.
                    var masked = reveal == true
                        ? []
                        : Masking.IndexesOf(columns, policies.For(conn));

                    return Results.Ok(new
                    {
                        columns = Masking.Describe(columns, masked),
                        rows = Masking.Apply(rows, masked),
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

        // The policy behind the masking. A word list gets a schema wrong somewhere; these two lists
        // are how somebody who knows their schema corrects it, once, for everybody.
        app.MapGet("/api/data/{conn}/mask-policy", (string conn, MaskPolicyStore policies) =>
        {
            var policy = policies.For(conn);
            return Results.Ok(new
            {
                maskByDefault = policy.MaskByDefault,
                extra = policy.Extra.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
                never = policy.Never.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
            });
        });

        app.MapPut("/api/data/{conn}/mask-policy", (string conn, MaskPolicyRequest body,
            MaskPolicyStore policies) =>
        {
            policies.Save(conn, new MaskPolicy(
                body.MaskByDefault ?? true,
                new HashSet<string>(body.Extra ?? [], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(body.Never ?? [], StringComparer.OrdinalIgnoreCase)));

            return Results.NoContent();
        });

        app.MapPost("/api/data/{conn}/preview-changes", async (string conn, [FromQuery(Name = "ref")] string objectRef,
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
                    cache.Set($"changes:{hash}",
                        new Prepared(target, script, changeSet, identity.KeyColumns),
                        TimeSpan.FromMinutes(10));

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

        app.MapPost("/api/data/{conn}/apply-changes", async (string conn, [FromQuery(Name = "ref")] string objectRef,
            ApplyRequest body, SessionFactory factory, IMemoryCache cache, UndoStore undo,
            CancellationToken ct) =>
        {
            if (cache.Get($"changes:{body.Hash}") is not Prepared prepared)
                return Results.Json(
                    new { message = "the preview expired or the data changed; preview again before applying" },
                    statusCode: StatusCodes.Status409Conflict);

            var script = prepared.Script;
            // Set when this apply is itself an undo, so the entry can be consumed once it worked.
            var undoing = cache.Get($"undo-of:{body.Hash}") as string;

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
                        // Read the rows this change is about to overwrite, inside the same
                        // transaction: anything read after the commit is somebody else's data.
                        var before = await Undo.CaptureAsync(session, transaction, driver.Dialect,
                            prepared.Target, prepared.Changes, ct);

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

                        if (undoing is not null)
                        {
                            undo.Consume(conn, undoing);
                            cache.Remove($"undo-of:{body.Hash}");
                        }

                        // The step is undoable only if the inverse could be built *and* stored.
                        // An undo is not itself recorded: one step back is a model people can hold,
                        // "undo the undo" is not.
                        var inverse = undoing is null
                            ? Undo.BuildInverse(prepared.Changes, prepared.KeyColumns, before)
                            : [];
                        var undoable = inverse.Count > 0 && undo.Push(conn, new UndoEntry(
                            Guid.NewGuid().ToString("n"), prepared.Target.ToString(),
                            Undo.Describe(prepared.Changes), DateTimeOffset.UtcNow, inverse));

                        return Results.Ok(new
                        {
                            applied, failedAt = (int?)null, error = (string?)null, undoable,
                        });
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

        // What could be undone here, so the button knows whether it has anything to offer.
        app.MapGet("/api/data/{conn}/undo", (string conn, [FromQuery(Name = "ref")] string objectRef,
            UndoStore undo) =>
        {
            var entry = undo.Newest(conn, objectRef);
            return entry is null
                ? Results.Ok(new { available = false, label = (string?)null, at = (DateTimeOffset?)null })
                : Results.Ok(new { available = true, label = entry.Label, at = entry.At });
        });

        // An undo is a change like any other, so it goes through the same preview-then-apply
        // handshake: the inverse script is shown first, and apply-changes executes it.
        app.MapPost("/api/data/{conn}/undo/preview", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, UndoStore undo,
            IMemoryCache cache, CancellationToken ct) =>
        {
            var entry = undo.Newest(conn, objectRef);
            if (entry is null)
                return Results.BadRequest(new { message = "there is nothing to undo on this table" });

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);
                    var identity = RowIdentity.Resolve(detail);

                    var changeSet = new ChangeSet(conn, target.ToString(), entry.Changes);
                    var script = ChangeScriptBuilder.Build(changeSet, detail, driver.Dialect);
                    var hash = changeSet.Hash();

                    cache.Set($"changes:{hash}",
                        new Prepared(target, script, changeSet, identity.KeyColumns),
                        TimeSpan.FromMinutes(10));
                    cache.Set($"undo-of:{hash}", entry.Id, TimeSpan.FromMinutes(10));

                    return Results.Ok(new
                    {
                        hash,
                        script = script.Text,
                        statementCount = script.Statements.Count,
                        destructive = script.Statements.Any(s => s.Destructive),
                        label = entry.Label,
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/data/{conn}/lookup", async (string conn, [FromQuery(Name = "ref")] string objectRef,
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
