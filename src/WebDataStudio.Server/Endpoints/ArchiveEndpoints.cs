using Microsoft.AspNetCore.Mvc;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Editing;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class ArchiveEndpoints
{
    /// What to keep: a statement's result, or a whole table. One of the two, never both.
    public record SaveRequest(string ConnectionId, string? Sql, string? ObjectRef, int? MaxRows);

    public static void MapArchiveEndpoints(this WebApplication app)
    {
        var defaultMaxRows = int.TryParse(app.Configuration["WDS_ARCHIVE_MAX_ROWS"], out var m)
            ? m
            : 100_000;
        var timeout = int.TryParse(app.Configuration["WDS_QUERY_TIMEOUT_SECONDS"], out var t) ? t : 300;

        app.MapGet("/api/archives", (Archives archives) => Results.Ok(new
        {
            available = archives.Available,
            path = archives.Path,
            error = archives.Error,
            items = archives.List(),
        }));

        app.MapGet("/api/archives/{name}", (string name, int? offset, int? limit, Archives archives) =>
        {
            try
            {
                return Results.Ok(archives.Read(name,
                    Math.Max(offset ?? 0, 0), Math.Clamp(limit ?? 200, 1, 5_000)));
            }
            catch (FileNotFoundException e) { return Results.NotFound(new { message = e.Message }); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // Keeping a result is keeping a copy of the data, so it is masked on the way in — exactly
        // like an export. An archive of a masked column would be a way around the masking.
        app.MapPost("/api/archives/{name}", async (string name, SaveRequest body,
            Archives archives, SessionFactory factory, MaskPolicyStore policies,
            CancellationToken ct) =>
        {
            if (body.Sql is null && body.ObjectRef is null)
                return Results.BadRequest(new { message = "give a statement or an object to archive" });

            try
            {
                var (driver, session) = await factory.OpenAsync(body.ConnectionId, ct);
                await using (session)
                {
                    var sql = body.Sql;

                    if (sql is null)
                    {
                        var target = SchemaEndpoints.ParseObjectRef(body.ObjectRef!);
                        sql = $"SELECT * FROM {ChangeScriptBuilder.Qualify(target, driver.Dialect)}";
                    }

                    var rows = Math.Clamp(body.MaxRows ?? defaultMaxRows, 1, 1_000_000);
                    var request = new ScriptRequest(sql, rows, timeout);

                    var info = await archives.SaveAsync(name,
                        body.ObjectRef ?? Shorten(sql),
                        Masking.Stream(driver.ExecuteAsync(session, request, ct),
                            policies.For(body.ConnectionId), ct),
                        rows, ct);

                    return Results.Ok(info);
                }
            }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapDelete("/api/archives/{name}", (string name, Archives archives) =>
        {
            try
            {
                return archives.Delete(name)
                    ? Results.NoContent()
                    : Results.NotFound(new { message = $"no archive named '{name}'" });
            }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        });

        // The rows again, as INSERT statements for wherever they should end up next. The studio's
        // own preview runs them, like every other change.
        app.MapPost("/api/archives/{name}/insert-script", (string name, string table,
            [FromQuery(Name = "connectionId")] string connectionId, Archives archives,
            ConnectionRegistry connections, int? limit) =>
        {
            try
            {
                var info = archives.Find(name)
                    ?? throw new FileNotFoundException($"no archive named '{name}'");

                var engine = connections.Find(connectionId)?.Engine ?? "postgresql";
                var take = Math.Clamp(limit ?? 1_000, 1, 50_000);

                return Results.Ok(new
                {
                    sql = ArchiveScript.Inserts(engine, table, info.Columns,
                        archives.ReadAll(name).Take(take)),
                    rows = Math.Min(take, info.Rows),
                    truncated = info.Rows > take,
                });
            }
            catch (FileNotFoundException e) { return Results.NotFound(new { message = e.Message }); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    /// Enough of a statement to recognise it later, without keeping the whole thing in a listing.
    private static string Shorten(string sql)
    {
        var line = sql.ReplaceLineEndings(" ").Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
