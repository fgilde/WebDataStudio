using Microsoft.AspNetCore.Mvc;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// A saved query, offered as a form somebody can fill in.
///
/// Reading only, and capped: a report is the thing a person who has never seen this database is going
/// to press, so it is not the place to find out that a saved query had a `DELETE` in it.
public static class ReportEndpoints
{
    public record RunRequest(Dictionary<string, string?>? Parameters, int? MaxRows);

    public static void MapReportEndpoints(this WebApplication app)
    {
        var defaultMaxRows = int.TryParse(app.Configuration["WDS_MAX_ROWS"], out var m) ? m : 1000;
        var timeout = int.TryParse(app.Configuration["WDS_QUERY_TIMEOUT_SECONDS"], out var t) ? t : 300;

        app.MapGet("/api/reports", (WorkspaceStore workspace, ConnectionRegistry connections) =>
            Results.Ok(Reports.All(workspace, connections).Select(report => new
            {
                report.Id,
                report.Name,
                report.Folder,
                report.ConnectionId,
                report.Parameters,
                // The statement itself, so somebody can see what they are about to run.
                report.Sql,
            })));

        app.MapPost("/api/reports/{id}/run", async (string id, RunRequest body,
            HttpContext ctx, WorkspaceStore workspace, ConnectionRegistry connections,
            SessionFactory factory, MaskPolicyStore policies, CancellationToken ct) =>
        {
            var report = Reports.All(workspace, connections)
                .FirstOrDefault(candidate => candidate.Id == id);

            if (report is null) return Results.NotFound(new { message = $"no report '{id}'" });

            try
            {
                var (driver, session) = await factory.OpenAsync(report.ConnectionId, ct);

                await using (session)
                {
                    // Reading only, whatever the saved query says. A report is pressed by people who
                    // are not reading the SQL.
                    if (!driver.Dialect.IsReadOnlyStatement(report.Sql))
                        return Results.BadRequest(new
                        {
                            message = "this saved query changes data, so it is not offered as a report",
                        });

                    var missing = report.Parameters
                        .Where(name => body.Parameters is null
                                       || !body.Parameters.ContainsKey(name)
                                       || string.IsNullOrEmpty(body.Parameters[name]))
                        .ToList();

                    if (missing.Count > 0)
                        return Results.BadRequest(new
                        {
                            message = $"this report needs {string.Join(", ", missing)}",
                            missing,
                        });

                    Audit.Detail(ctx, $"report {report.Name}", report.ConnectionId);

                    var rows = Math.Clamp(body.MaxRows ?? defaultMaxRows, 1, 100_000);
                    var request = new ScriptRequest(report.Sql, rows, timeout,
                        Parameters: body.Parameters);

                    var columns = new List<object>();
                    var values = new List<object?[]>();
                    var masked = new HashSet<int>();
                    string? error = null;
                    var truncated = false;

                    var chunks = Masking.Stream(driver.ExecuteAsync(session, request, ct),
                        policies.For(report.ConnectionId), ct);

                    await foreach (var chunk in chunks.WithCancellation(ct))
                        switch (chunk)
                        {
                            case ResultChunk.Columns c:
                                columns.Clear();
                                masked.Clear();
                                columns.AddRange(c.Items.Select(column => new
                                {
                                    column.Name,
                                    column.DataType,
                                }));
                                break;

                            case ResultChunk.Rows r:
                                values.AddRange(r.Items);
                                break;

                            case ResultChunk.End end:
                                truncated |= end.Truncated;
                                break;

                            case ResultChunk.Error e:
                                error = e.Text;
                                break;
                        }

                    return error is null
                        ? Results.Ok(new
                        {
                            report.Name,
                            columns,
                            rows = values,
                            truncated,
                        })
                        : Results.Json(new { message = error }, statusCode: 502);
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }
}
