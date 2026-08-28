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
    public record GenerateDto(int? Rows, Dictionary<string, string>? Strategies, int? Seed);

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
        // What is actually inside a JSON column: which paths exist, how often, with which types.
        // A JSONB column is one cell of text in the grid otherwise, and reading one row of it is a
        // guess.
        app.MapGet("/api/data/{conn}/json", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, string column, int? sample,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);

                    // Only a column that exists: everything else here is interpolated into SQL.
                    if (detail.Columns.All(c => !c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                        return Results.BadRequest(new { message = $"no column '{column}'" });

                    if (driver.FromClause(session, target) is not { } from)
                        return Results.BadRequest(new { message = "this object cannot be read" });

                    var report = await JsonShape.DescribeAsync(driver, session, from, column,
                        sample ?? JsonShape.DefaultSample, ct);

                    return Results.Ok(new
                    {
                        report.Sampled,
                        report.Parsed,
                        report.Note,
                        // Each path with the SQL that reads it on this engine: the panel copies it
                        // or builds a SELECT from it, and neither has to know one engine's spelling
                        // from another's.
                        paths = report.Paths.Select(path => new
                        {
                            path.Path,
                            path.Types,
                            path.Present,
                            path.Example,
                            expression = JsonShape.Expression(driver.Dialect, column, path.Path),
                        }),
                        // The SELECT that turns the paths into columns, ready for a query tab.
                        flatten = JsonShape.FlattenSql(driver.Dialect, from, column, report.Paths),
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/data/{conn}", async (string conn, [FromQuery(Name = "ref")] string objectRef,
            int? offset, int? limit, string? sort, bool? desc, string? filterColumn, string? filter,
            bool? reveal, [FromQuery(Name = "lookup")] string[]? lookup, SessionFactory factory,
            MaskPolicyStore policies, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    // A Redis key is one value, not a page of rows. Building `SELECT * FROM key`
                    // for it produced a command the server rejected, which told the user nothing.
                    if (!driver.Caps.TabularBrowse)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} has no rows to browse; open the key in " +
                                      "the key browser instead",
                        });

                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);
                    var identity = RowIdentity.Resolve(detail);

                    // A table is selected from by name; a file in a bucket by a reader over it.
                    // The driver says which, and a file no reader understands says so instead of
                    // producing SQL that fails.
                    if (driver.FromClause(session, target) is not { } table)
                        return Results.BadRequest(new
                        {
                            message = "this object cannot be read as a table; open the preview or " +
                                      "download it instead",
                        });

                    var take = Math.Clamp(limit ?? defaultLimit, 1, 100_000);
                    var skip = Math.Max(offset ?? 0, 0);

                    // A column from the table on the other side of a foreign key, shown here rather
                    // than reached by following it: "orders.customer_id.name" next to the id.
                    var lookups = await LookupsAsync(driver, session, detail, lookup ?? [], ct);
                    var alias = lookups.Count > 0 ? BaseAlias : null;

                    // Filter values are parameterised; only identifiers are interpolated, and those
                    // are checked against the real column list before they go anywhere near SQL.
                    var columnNames = detail.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var where = "";
                    var parameters = new Dictionary<string, object?>();

                    if (filterColumn is { Length: > 0 } && columnNames.Contains(filterColumn)
                        && filter is { Length: > 0 })
                    {
                        // The filter is a small language rather than a substring: see
                        // FilterExpression. A plain word still means "contains", which is what it
                        // meant before.
                        var column = detail.Columns
                            .First(c => c.Name.Equals(filterColumn, StringComparison.OrdinalIgnoreCase));

                        var condition = FilterExpression.Build(driver.Dialect,
                            Address(driver, alias, column.Name),
                            FilterExpression.KindOf(column.DataType), filter, "f");

                        if (!condition.IsEmpty)
                        {
                            where = $" WHERE {condition.Sql}";
                            foreach (var (key, value) in condition.Parameters) parameters[key] = value;
                        }
                    }

                    var order = sort is { Length: > 0 } && columnNames.Contains(sort)
                        ? $" ORDER BY {Address(driver, alias, sort)}{(desc == true ? " DESC" : "")}"
                        : "";

                    // Without lookups the statement stays exactly what it was: no alias, no join,
                    // `SELECT *`. With them the base table needs a name to join against.
                    var projection = alias is null
                        ? "*"
                        : $"{alias}.*, {string.Join(", ", lookups.Select(l => l.Projection))}";

                    var from = alias is null
                        ? table
                        : $"{table} {alias}{string.Concat(lookups.Select(l => l.Join))}";

                    var sql = driver.Dialect.Paginate(
                        $"SELECT {projection} FROM {from}{where}{order}", skip, take);
                    var request = new ScriptRequest(sql, take, timeout,
                        Parameters: FilterExpression.AsText(parameters));

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
                        // Which of the columns came from another table. They are read-only: an
                        // edit here would be an update to a row this grid is not addressing.
                        lookups = lookups.Select(l => l.Name),
                        offset = skip,
                        limit = take,
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // The values a column actually holds, most common first — the checkbox list that saves
        // guessing what to type into the filter. A masked column is refused rather than counted:
        // the distinct values of a column of secrets are the secrets.
        app.MapGet("/api/data/{conn}/distinct", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, string column, string? search, int? limit,
            SessionFactory factory, MaskPolicyStore policies, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.TabularBrowse)
                        return Results.BadRequest(new { message = $"{driver.Info.Label} has no columns to count" });

                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);

                    var found = detail.Columns
                        .FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

                    if (found is null)
                        return Results.BadRequest(new { message = $"no column named '{column}'" });

                    if (SensitiveColumns.ShouldMask(found.Name, policies.For(conn)))
                        return Results.Ok(new { masked = true, values = Array.Empty<object>(), truncated = false });

                    var take = Math.Clamp(limit ?? 200, 1, 1000);
                    var quoted = driver.Dialect.QuoteIdentifier(found.Name);
                    var parameters = new Dictionary<string, object?>();
                    var where = "";

                    // The search box narrows the list rather than paging through it: a column with
                    // a hundred thousand values is not something to scroll.
                    if (search is { Length: > 0 })
                    {
                        var condition = FilterExpression.Build(driver.Dialect, quoted,
                            FilterExpression.KindOf(found.DataType), search, "d");

                        if (!condition.IsEmpty)
                        {
                            where = $" WHERE {condition.Sql}";
                            foreach (var (key, value) in condition.Parameters) parameters[key] = value;
                        }
                    }

                    var sql = driver.Dialect.Paginate(
                        $"SELECT {quoted}, count(*) AS n FROM {driver.FromClause(session, target)}"
                        + $"{where} GROUP BY {quoted} ORDER BY n DESC", 0, take + 1);

                    var values = new List<object>();
                    await foreach (var chunk in driver.ExecuteAsync(session,
                        new ScriptRequest(sql, take + 1, timeout, Parameters: FilterExpression.AsText(parameters)), ct))
                    {
                        if (chunk is ResultChunk.Error error)
                            return Results.Json(new { message = error.Text }, statusCode: 502);

                        if (chunk is not ResultChunk.Rows rows) continue;

                        foreach (var row in rows.Items)
                            values.Add(new { value = row.ElementAtOrDefault(0), count = row.ElementAtOrDefault(1) });
                    }

                    // One more was asked for than is shown, which is how "there are more" is known
                    // without counting the whole column.
                    var truncated = values.Count > take;

                    return Results.Ok(new
                    {
                        masked = false,
                        values = values.Take(take),
                        truncated,
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
            var baseline = policies.Baseline;

            return Results.Ok(new
            {
                maskByDefault = policy.MaskByDefault,
                extra = policy.Extra.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
                never = policy.Never.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
                // What the deployment set, so the UI can say "this one is not yours to change here".
                fromEnvironment = new
                {
                    extra = baseline.Extra.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
                    never = baseline.Never.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
                },
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

        // What each column of this table would be filled with, so the dialog can offer the guess
        // and let somebody correct it.
        app.MapGet("/api/data/{conn}/generate/strategies", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);

                    return Results.Ok(new
                    {
                        available = DataGenerator.Strategies,
                        columns = detail.Columns.OrderBy(c => c.Position).Select(c => new
                        {
                            name = c.Name,
                            dataType = c.DataType,
                            nullable = c.Nullable,
                            strategy = DataGenerator.Infer(c, detail),
                        }),
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // Generated rows are ordinary inserts, so they are previewed and applied by the same
        // handshake as a hand edit: the script is shown first, and `apply-changes` runs it.
        app.MapPost("/api/data/{conn}/generate/preview", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, GenerateDto body, SessionFactory factory,
            IMemoryCache cache, CancellationToken ct) =>
        {
            var rows = body.Rows ?? 50;
            if (rows is < 1 or > 10_000)
                return Results.BadRequest(new { message = "between 1 and 10000 rows, please" });

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

                    // A foreign key can only point at rows that exist, so the parents are read
                    // first and the generator picks from them.
                    var parents = await ParentValuesAsync(driver, session, detail, ct);

                    var changes = DataGenerator.Build(detail,
                        new GenerateRequest(target.ToString(), rows, body.Strategies, body.Seed),
                        parents);

                    if (changes.Count == 0)
                        return Results.BadRequest(new
                        {
                            message = "nothing to insert: every column of this table is generated " +
                                      "by the database itself",
                        });

                    var changeSet = new ChangeSet(conn, target.ToString(), changes);
                    var script = ChangeScriptBuilder.Build(changeSet, detail, driver.Dialect);
                    var hash = changeSet.Hash();

                    cache.Set($"changes:{hash}",
                        new Prepared(target, script, changeSet, identity.KeyColumns),
                        TimeSpan.FromMinutes(10));

                    return Results.Ok(new
                    {
                        hash,
                        script = script.Text,
                        statementCount = script.Statements.Count,
                        destructive = false,
                        emptyForeignKeys = detail.ForeignKeys
                            .SelectMany(fk => fk.Columns)
                            .Where(column => !parents.ContainsKey(column) || parents[column].Count == 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase),
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
                    var table = driver.FromClause(session, target);

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

    /// One column borrowed from the table a foreign key points at.
    private sealed record Lookup(string Name, string Projection, string Join);

    /// The name the base table gets once something is joined to it. Both the join and every column
    /// reference use it, so it lives in one place.
    private const string BaseAlias = "wds_t";

    /// How a column of the base table is addressed. With a lookup in play the table has an alias,
    /// and an unqualified name would be ambiguous the moment the other table has one like it.
    private static string Address(IDbDriver driver, string? alias, string column) =>
        alias is null
            ? driver.Dialect.QuoteIdentifier(column)
            : $"{alias}.{driver.Dialect.QuoteIdentifier(column)}";

    /// Turns `customer_id.name` into a join and a column. Anything that does not name a real
    /// single-column foreign key and a real column on the other side is dropped: this comes from a
    /// query string, and it ends up in SQL.
    private static async Task<List<Lookup>> LookupsAsync(IDbDriver driver, IDbSession session,
        ObjectDetail detail, string[] requested, CancellationToken ct)
    {
        var lookups = new List<Lookup>();

        // Four is already an unusual grid; the cap is there so a crafted query string cannot ask
        // for fifty joins.
        foreach (var spec in requested.Take(8))
        {
            var dot = spec.LastIndexOf('.');
            if (dot <= 0 || dot == spec.Length - 1) continue;

            var from = spec[..dot];
            var wanted = spec[(dot + 1)..];

            // A composite key cannot be followed with one column, so it is not offered.
            var key = detail.ForeignKeys.FirstOrDefault(fk =>
                fk.Columns.Count == 1 && fk.ReferencedColumns.Count == 1
                && fk.Columns[0].Equals(from, StringComparison.OrdinalIgnoreCase));

            if (key is null) continue;

            var targetRef = new SchemaNodeRef(SchemaNodeKind.Table,
                [key.ReferencedSchema, key.ReferencedTable]);

            ObjectDetail target;
            try { target = await driver.DescribeAsync(session, targetRef, ct); }
            catch (Exception) { continue; } // a table this account cannot read is not a lookup

            var column = target.Columns
                .FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));

            if (column is null) continue;

            var alias = $"wds_l{lookups.Count}";
            var name = $"{key.Columns[0]}.{column.Name}";

            lookups.Add(new Lookup(
                name,
                $"{alias}.{driver.Dialect.QuoteIdentifier(column.Name)} AS " +
                driver.Dialect.QuoteIdentifier(name),
                $" LEFT JOIN {ChangeScriptBuilder.Qualify(targetRef, driver.Dialect)} {alias}" +
                $" ON {alias}.{driver.Dialect.QuoteIdentifier(key.ReferencedColumns[0])}" +
                $" = {BaseAlias}.{driver.Dialect.QuoteIdentifier(key.Columns[0])}"));
        }

        return lookups;
    }

    /// The type name every engine accepts in a CAST to text.
    private static string CharType(IDbDriver driver) => driver.Info.Id switch
    {
        "sqlserver" => "NVARCHAR(MAX)",
        "mysql" => "CHAR",
        "oracle" => "VARCHAR2(4000)",
        _ => "TEXT",
    };

    /// Up to 200 existing values per foreign-key column, to point generated rows at.
    private static async Task<Dictionary<string, IReadOnlyList<object?>>> ParentValuesAsync(
        IDbDriver driver, IDbSession session, ObjectDetail detail, CancellationToken ct)
    {
        var parents = new Dictionary<string, IReadOnlyList<object?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in detail.ForeignKeys)
        {
            for (var i = 0; i < key.Columns.Count && i < key.ReferencedColumns.Count; i++)
            {
                var parent = new SchemaNodeRef(SchemaNodeKind.Table,
                    key.ReferencedSchema is { Length: > 0 }
                        ? [key.ReferencedSchema, key.ReferencedTable]
                        : [key.ReferencedTable]);

                var column = driver.Dialect.QuoteIdentifier(key.ReferencedColumns[i]);
                var sql = $"SELECT DISTINCT {column} FROM {ChangeScriptBuilder.Qualify(parent, driver.Dialect)} " +
                          $"WHERE {column} IS NOT NULL";

                var values = new List<object?>();

                try
                {
                    await foreach (var chunk in driver.ExecuteAsync(session, new ScriptRequest(sql, 200, 60), ct))
                        if (chunk is ResultChunk.Rows rows)
                            values.AddRange(rows.Items.Select(row => row.Length > 0 ? row[0] : null));
                }
                catch (Exception)
                {
                    // A parent that cannot be read is a foreign key the generator leaves alone.
                }

                parents[key.Columns[i]] = values;
            }
        }

        return parents;
    }

    private static ChangeSet ToChangeSet(string connectionId, string objectRef, ChangeRequest body) =>
        new(connectionId, objectRef, body.Changes.Select(c => new RowChange(
            c.Kind,
            c.Key.ToDictionary(k => k.Key, k => (object?)k.Value),
            c.Values.ToDictionary(k => k.Key, k => (object?)k.Value))).ToList());
}
