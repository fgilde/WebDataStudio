using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class AnalysisEndpoints
{
    public record AnalyzeQueryRequest(string ConnectionId, string Sql, bool? Actual);

    public static void MapAnalysisEndpoints(this WebApplication app)
    {
        app.MapPost("/api/query/analyze", async (AnalyzeQueryRequest body, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(body.ConnectionId, ct);
                await using (session)
                {
                    PlanNode? plan = null;
                    string? planError = null;

                    if (driver.Caps.EstimatedPlan)
                    {
                        try
                        {
                            var mode = body.Actual == true && driver.Caps.ActualPlan
                                ? PlanMode.Actual : PlanMode.Estimated;
                            plan = await driver.ExplainAsync(session, body.Sql, mode, ct);
                        }
                        catch (Exception e)
                        {
                            // A plan the engine refuses is worth reporting, but the SQL-only advice
                            // below still works without it.
                            planError = e.Message;
                        }
                    }

                    var tables = await LoadTablesAsync(driver, session, body.Sql, ct);
                    var findings = new List<AnalyzeFinding>();

                    if (plan is not null) findings.AddRange(PlanRules.Evaluate(plan));
                    findings.AddRange(IndexAdvisor.Suggest(body.Sql, plan, tables, driver.Dialect));

                    return Results.Ok(new
                    {
                        plan,
                        summary = plan is null ? null : PlanSummaryBuilder.Summarize(plan),
                        planError,
                        findings = Deduplicate(findings),
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/analyze/{conn}", async (string conn, string? schema,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = schema is { Length: > 0 }
                        ? new SchemaNodeRef(SchemaNodeKind.Schema, [schema])
                        : null;

                    var report = await driver.AnalyzeAsync(session, AnalyzeScope.Schema, target, ct);
                    return Results.Ok(new { report.Findings });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/stats/{conn}", async (string conn, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.ServerStats)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} does not expose server statistics",
                        });

                    return Results.Ok(await ServerStatistics.ReadAsync(driver, session, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/stats/{conn}/slow-queries", async (string conn, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.SlowQueryLog)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} does not expose a slow query log",
                        });

                    return Results.Ok(await ServerStatistics.SlowQueriesAsync(driver, session, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    /// Describes only the tables the statement actually mentions, so the advisor never walks a
    /// whole catalogue to answer one query.
    /// Rules about the data rather than about the schema, and the results of running them.
    public static void MapQualityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/quality/{conn}", (string conn, QualityRunner rules) =>
            Results.Ok(rules.For(conn)));

        app.MapPut("/api/quality/{conn}", (string conn, QualityRule body, QualityRunner rules) =>
        {
            if (body.Table.Trim().Length == 0)
                return Results.BadRequest(new { message = "a rule needs a table" });

            var rule = body with
            {
                ConnectionId = conn,
                Id = body.Id is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N")[..12],
            };

            rules.Save(rule);
            return Results.Ok(rule);
        });

        app.MapDelete("/api/quality/{conn}/{id}", (string conn, string id, QualityRunner rules) =>
        {
            try
            {
                rules.Delete(id);
                return Results.NoContent();
            }
            catch (InvalidOperationException e) { return Results.BadRequest(new { message = e.Message }); }
        });

        // What each rule counted, over time. A rule's answer is a count, and a count over time is the
        // difference between "twelve rows are wrong" and "it is getting worse".
        app.MapGet("/api/quality/{conn}/history", (string conn, int? days, QualityRunner rules,
            WorkspaceStore workspace) =>
        {
            var window = Math.Clamp(days ?? 30, 1, 365);

            if (!workspace.Available)
                return Results.Ok(new { days = window, rules = Array.Empty<object>() });

            var runs = workspace.ListQualityRuns(conn, DateTimeOffset.UtcNow.AddDays(-window));
            var known = rules.For(conn).ToDictionary(rule => rule.Id);

            return Results.Ok(new
            {
                days = window,
                rules = runs
                    .GroupBy(run => run.RuleId)
                    .Select(group => new
                    {
                        ruleId = group.Key,
                        table = known.TryGetValue(group.Key, out var rule) ? rule.Table : null,
                        column = known.TryGetValue(group.Key, out var named) ? named.Column : null,
                        kind = known.TryGetValue(group.Key, out var kind) ? kind.Kind.ToString() : null,
                        runs = group.Count(),
                        first = group.First().Violations,
                        last = group.Last().Violations,
                        worst = group.Max(run => run.Violations),
                        // Better, worse or the same: the sentence somebody actually needs.
                        trend = QualityRules.Describe(group.First().Violations, group.Last().Violations),
                        points = group.Select(run => new
                        {
                            at = run.At,
                            run.Violations,
                            failed = run.Error is { Length: > 0 },
                        }),
                    })
                    .OrderByDescending(entry => entry.last)
                    .ToList(),
            });
        });

        app.MapPost("/api/quality/{conn}/run", async (string conn, QualityRunner rules,
            CancellationToken ct) =>
        {
            try
            {
                var results = await rules.RunAsync(conn, ct);

                return Results.Ok(new
                {
                    ran = results.Count,
                    failing = results.Count(result => !result.Passed),
                    results,
                });
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    /// The tables a statement mentions. The walk itself lives in Analysis/TableLoader.cs, because
    /// the capture's advice needs exactly the same thing.
    private static Task<Dictionary<string, ObjectDetail>> LoadTablesAsync(IDbDriver driver,
        IDbSession session, string sql, CancellationToken ct) =>
        TableLoader.LoadAsync(driver, session, sql, ct);

    private static IReadOnlyList<AnalyzeFinding> Deduplicate(IEnumerable<AnalyzeFinding> findings) =>
        findings
            .GroupBy(f => (f.Category, f.Title), StringComparer.OrdinalIgnoreCase as IEqualityComparer<(string, string)>)
            .Select(g => g.First())
            .OrderBy(f => f.Severity == "critical" ? 0 : f.Severity == "warning" ? 1 : 2)
            .ThenBy(f => f.Title, StringComparer.Ordinal)
            .ToList();
}
