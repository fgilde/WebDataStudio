using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class QueryEndpoints
{
    public record ExecuteRequest(string ConnectionId, string Sql, int? MaxRows, int? TimeoutSeconds,
        string? Schema, Dictionary<string, string?>? Parameters, bool? Transactional = null);

    public record PlanRequest(string ConnectionId, string Sql, string Mode);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapQueryEndpoints(this WebApplication app)
    {
        var defaultMaxRows = int.TryParse(app.Configuration["WDS_MAX_ROWS"], out var m) ? m : 1000;
        var defaultTimeout = int.TryParse(app.Configuration["WDS_QUERY_TIMEOUT_SECONDS"], out var t) ? t : 300;

        app.MapPost("/api/query/execute", async (ExecuteRequest body, HttpContext ctx,
            SessionFactory factory, QueryRunner runner, MaskPolicyStore policies) =>
        {
            IDbDriver driver;
            IDbSession session;
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

            using var span = Telemetry.Span("query.execute");
            span?.SetTag("engine", driver.Info.Id);

            var started = System.Diagnostics.Stopwatch.StartNew();
            var returned = 0L;
            var failed = false;

            var (runId, source) = runner.Start(ctx.RequestAborted);
            ctx.Response.Headers["X-Run-Id"] = runId;
            ctx.Response.ContentType = "application/x-ndjson";

            var request = new ScriptRequest(body.Sql, body.MaxRows ?? defaultMaxRows,
                body.TimeoutSeconds ?? defaultTimeout, body.Schema, body.Parameters,
                body.Transactional ?? false);

            // A query is the other way into the same data as the data tab, so it cannot be the way
            // around the mask policy. The columns chunk decides which indexes are hidden; the row
            // chunks that follow it are masked at those indexes.
            var policy = policies.For(body.ConnectionId);
            var masked = new HashSet<int>();

            await using (session)
            {
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
