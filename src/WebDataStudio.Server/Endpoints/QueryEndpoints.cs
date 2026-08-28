using System.Text.Json;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class QueryEndpoints
{
    public record ExecuteRequest(string ConnectionId, string Sql, int? MaxRows, int? TimeoutSeconds,
        string? Schema, Dictionary<string, string?>? Parameters, bool? Transactional = null,
        /// A transaction this tab is holding open — see OpenTransactions. The statements run inside
        /// it, and nothing is committed until somebody says so.
        string? TransactionId = null,
        /// Keep going after a statement fails, and report what failed at the end. Off by default:
        /// stopping at the first error is what a migration wants.
        bool? ContinueOnError = null);

    public record BeginRequest(string ConnectionId);

    public record PlanRequest(string ConnectionId, string Sql, string Mode);

    public record InspectRequest(string ConnectionId, string Sql);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapQueryEndpoints(this WebApplication app)
    {
        var defaultMaxRows = int.TryParse(app.Configuration["WDS_MAX_ROWS"], out var m) ? m : 1000;
        var defaultTimeout = int.TryParse(app.Configuration["WDS_QUERY_TIMEOUT_SECONDS"], out var t) ? t : 300;

        // A read of the SQL before it runs: an UPDATE with no WHERE, an accidental cross product,
        // = NULL. It warns and never refuses — every one of these is something a person can
        // legitimately mean, and refusing would only teach people to bypass it.
        app.MapPost("/api/query/inspect", (InspectRequest body, ConnectionRegistry connections,
            DriverRegistry drivers) =>
        {
            var engine = connections.Find(body.ConnectionId)?.Engine;
            var dialect = engine is { Length: > 0 } known
                ? drivers.Get(known).Dialect
                // No connection chosen yet: the checks that matter here are the same in every
                // dialect, so a default is better than a refusal.
                : drivers.Get("postgresql").Dialect;

            return Results.Ok(SqlInspections.Inspect(body.Sql, dialect));
        });

        app.MapPost("/api/query/execute", async (ExecuteRequest body, HttpContext ctx,
            SessionFactory factory, QueryRunner runner, MaskPolicyStore policies,
            Archives archives, SafetyOptions safety, OpenTransactions transactions) =>
        {
            IDbDriver driver;
            IDbSession session;

            // A tab holding a transaction open runs on that session, and it is not disposed here:
            // the transaction outlives the request, and closing it is a deliberate second call.
            var held = body.TransactionId is { Length: > 0 } id ? transactions.Use(id, body.Sql) : null;

            if (body.TransactionId is { Length: > 0 } && held is null)
                return Results.Json(new
                {
                    message = "this transaction is not open any more; it was committed, rolled back "
                              + "or timed out",
                }, statusCode: StatusCodes.Status409Conflict);

            if (held is { } open)
            {
                (driver, session) = (open.Driver, open.Session);
            }
            else
            {
                try
                {
                    (driver, session) = await factory.OpenAsync(body.ConnectionId, ctx.RequestAborted);
                }
                catch (UnknownConnectionException e)
                {
                    return Results.NotFound(new { message = e.Message });
                }
                catch (Exception e)
                {
                    return Results.Json(new { message = e.Message }, statusCode: 502);
                }
            }

            using var span = Telemetry.Span("query.execute");
            span?.SetTag("engine", driver.Info.Id);

            // The route says a statement was run; only this knows which one and against what.
            Audit.Detail(ctx, body.Sql, body.ConnectionId);

            var started = System.Diagnostics.Stopwatch.StartNew();
            var returned = 0L;
            var failed = false;

            var (runId, source) = runner.Start(ctx.RequestAborted);
            ctx.Response.Headers["X-Run-Id"] = runId;
            ctx.Response.ContentType = "application/x-ndjson";

            var request = new ScriptRequest(body.Sql, body.MaxRows ?? defaultMaxRows,
                body.TimeoutSeconds ?? defaultTimeout, body.Schema, body.Parameters,
                // A held transaction is already open, so the per-script one would nest.
                body.Transactional == true && held is null,
                body.ContinueOnError ?? false);

            // A query is the other way into the same data as the data tab, so it cannot be the way
            // around the mask policy. The columns chunk decides which indexes are hidden; the row
            // chunks that follow it are masked at those indexes.
            var policy = policies.For(body.ConnectionId);
            var masked = new HashSet<int>();

            // `await using` on a held session would hand it back to the pool mid-transaction.
            await using (held is null ? session : null)
            {
                // A statement that takes every row gets a copy of them first: the archive is a file
                // the studio can list, reopen and script back out as inserts, which is the only
                // undo a DELETE has ever had. Read on this session before the statement runs, so
                // the order is "keep, then take" whatever happens next.
                var kept = new List<KeptRows>();

                if (safety.Enabled && archives.Available)
                    try
                    {
                        var sweeping = SafetyNet.Sweeping(body.Sql, driver.Dialect);

                        kept.AddRange(await SafetyNet.KeepAsync(driver, session, archives, policy,
                            sweeping, safety, body.TimeoutSeconds ?? defaultTimeout, source.Token));

                        foreach (var one in kept)
                            await WriteAsync(ctx,
                                Wire(new ResultChunk.Message(0, "info", one.Describe()), masked),
                                source.Token);
                    }
                    catch (Exception e)
                    {
                        // A copy that could not be taken is worth saying so, and worth saying before
                        // the statement runs — but it is not a reason to refuse a statement somebody
                        // asked for. WDS_SAFETY_NET=false is how to mean it every time.
                        await WriteAsync(ctx, Wire(new ResultChunk.Message(0, "warning",
                            $"the rows could not be kept first: {e.Message}"), masked), source.Token);
                    }

                if (kept.Count > 0)
                    Audit.Detail(ctx,
                        body.Sql + "\n-- kept first: " + string.Join(", ", kept.Select(k => k.Archive)),
                        body.ConnectionId);

                try
                {
                    await foreach (var chunk in driver.ExecuteAsync(session, request, source.Token))
                    {
                        switch (chunk)
                        {
                            case ResultChunk.Columns columns:
                                masked.Clear();
                                masked.UnionWith(Masking.IndexesOf(columns.Items, policy));
                                break;

                            case ResultChunk.Rows rows: returned += rows.Items.Count; break;
                            case ResultChunk.Error: failed = true; break;
                        }

                        await WriteAsync(ctx, Wire(chunk, masked), source.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // A cancelled run is a normal outcome; tell the client so it can mark the tab.
                    await WriteAsync(ctx, new { type = "cancelled" }, CancellationToken.None);
                }
                finally
                {
                    runner.Finish(runId);

                    Telemetry.Query(driver.Info.Id, failed, started.Elapsed.TotalMilliseconds, returned);
                    span?.SetTag("rows", returned);
                    span?.SetTag("failed", failed);
                }
            }

            return Results.Empty;
        });

        // --- transactions somebody holds open ----------------------------------------------------
        // Auto-commit stays the default. This is the other mode: BEGIN, look at what the statements
        // did, then commit or roll the whole thing back.
        app.MapPost("/api/tx/begin", async (BeginRequest body, SessionFactory factory,
            OpenTransactions transactions, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(body.ConnectionId, ct);

                try
                {
                    return Results.Ok(await transactions.BeginAsync(body.ConnectionId, driver, session, ct));
                }
                catch
                {
                    // Nothing was held, so the session goes straight back rather than leaking a slot.
                    await session.DisposeAsync();
                    throw;
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/tx/{id}/commit", async (string id, HttpContext ctx,
            OpenTransactions transactions) =>
        {
            Audit.Detail(ctx, $"commit transaction {id}", transactions.Find(id)?.ConnectionId);

            return await transactions.CommitAsync(id)
                ? Results.Ok(new { committed = true })
                : Results.NotFound(new { message = "this transaction is not open any more" });
        });

        app.MapPost("/api/tx/{id}/rollback", async (string id, HttpContext ctx,
            OpenTransactions transactions) =>
        {
            Audit.Detail(ctx, $"roll back transaction {id}", transactions.Find(id)?.ConnectionId);

            return await transactions.RollbackAsync(id)
                ? Results.Ok(new { rolledBack = true })
                : Results.NotFound(new { message = "this transaction is not open any more" });
        });

        // What is open right now, so a transaction cannot be forgotten quietly.
        app.MapGet("/api/tx", (OpenTransactions transactions) => Results.Ok(new
        {
            idleTimeoutSeconds = (int)transactions.IdleTimeout.TotalSeconds,
            open = transactions.All(),
        }));

        // The schedule, what it last did, and a way to run one now. Absent state rather than an
        // error when no schedule file is configured: the UI can ask without knowing.
        app.MapGet("/api/schedule", (ScheduledQueries queries) => Results.Ok(new
        {
            configured = queries.Configured,
            jobs = queries.Read().Select(job => new
            {
                job.Name,
                job.Connection,
                job.Sql,
                job.EveryMinutes,
                job.DailyAtUtc,
                format = job.Format ?? "csv",
            }),
            runs = queries.Runs,
        }));

        app.MapPost("/api/schedule/{name}/run", async (string name, ScheduledQueries queries,
            CancellationToken ct) =>
        {
            var job = queries.Read().FirstOrDefault(candidate =>
                candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (job is null)
                return Results.NotFound(new { message = $"there is no scheduled query called '{name}'" });

            var run = await queries.RunAsync(job, ct);

            return run.Error is null ? Results.Ok(run) : Results.BadRequest(run);
        });

        app.MapPost("/api/query/{runId}/cancel", (string runId, QueryRunner runner) =>
            runner.Cancel(runId) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/query/plan", async (PlanRequest body, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(body.ConnectionId, ct);
                await using (session)
                {
                    var mode = body.Mode.Equals("actual", StringComparison.OrdinalIgnoreCase)
                        ? PlanMode.Actual : PlanMode.Estimated;

                    // The plan alone. `/api/query/analyze` is the one that reads it — one analyser,
                    // in Analysis/PlanRules, rather than two that drift apart.
                    return Results.Ok(await driver.ExplainAsync(session, body.Sql, mode, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    private static async Task WriteAsync(HttpContext ctx, object payload, CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, Json, ct);
        await ctx.Response.Body.WriteAsync("\n"u8.ToArray(), ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    /// The wire shape from spec section 5.3. Kept separate from ResultChunk so the record
    /// hierarchy can change without breaking the client contract.
    private static object Wire(ResultChunk chunk, HashSet<int> masked) => chunk switch
    {
        ResultChunk.Columns c => new
        {
            type = "columns", statement = c.Statement, columns = Masking.Describe(c.Items, masked),
        },
        ResultChunk.Rows r => new
        {
            type = "rows", statement = r.Statement, rows = Masking.Apply(r.Items, masked),
        },
        ResultChunk.Documents d => new { type = "documents", statement = d.Statement, documents = d.Items },
        ResultChunk.Progress p => new { type = "progress", statement = p.Statement, rowsRead = p.RowsRead, elapsedMs = p.ElapsedMs },
        ResultChunk.Message m => new { type = "message", statement = m.Statement, severity = m.Severity, text = m.Text },
        ResultChunk.End e => new { type = "end", statement = e.Statement, rowsAffected = e.RowsAffected, elapsedMs = e.ElapsedMs, truncated = e.Truncated },
        ResultChunk.Error x => new { type = "error", statement = x.Statement, text = x.Text, code = x.Code, line = x.Line, column = x.Column },
        _ => new { type = "unknown" },
    };
}
