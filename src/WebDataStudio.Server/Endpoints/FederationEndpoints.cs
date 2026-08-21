using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class FederationEndpoints
{
    public record SourceDto(string ConnectionId, string Sql, string Alias);
    public record FederateRequest(List<SourceDto> Sources, string Sql, int? MaxRowsPerSource);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapFederationEndpoints(this WebApplication app)
    {
        // What would be staged, without copying anything: the tables DuckDB would create per
        // source. A wrong alias or a broken source query shows up here rather than after a minute
        // of copying.
        app.MapPost("/api/federate/preview", async (FederateRequest body, Federation federation,
            CancellationToken ct) =>
        {
            try
            {
                var plan = await federation.PreviewAsync(Model(body), ct);
                return Results.Ok(new
                {
                    sources = plan.Select(entry => new { alias = entry.Alias, ddl = entry.Ddl }),
                });
            }
            catch (FederationException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // The same NDJSON stream a query produces, so the existing result grid renders it without
        // knowing that several databases were involved.
        app.MapPost("/api/federate/run", async (FederateRequest body, HttpContext ctx,
            Federation federation) =>
        {
            var ct = ctx.RequestAborted;
            ctx.Response.ContentType = "application/x-ndjson";

            try
            {
                await foreach (var chunk in federation.RunAsync(Model(body), ct))
                    await WriteAsync(ctx, Wire(chunk), ct);
            }
            catch (FederationException e)
            {
                // The stream has already started, so the failure travels in it rather than as a
                // status code the client would never see.
                await WriteAsync(ctx, new { type = "error", statement = 0, text = e.Message },
                    CancellationToken.None);
            }
            catch (UnknownConnectionException e)
            {
                await WriteAsync(ctx, new { type = "error", statement = 0, text = e.Message },
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                await WriteAsync(ctx, new { type = "cancelled" }, CancellationToken.None);
            }

            return Results.Empty;
        });
    }

    private static FederationRequest Model(FederateRequest body) => new(
        [.. (body.Sources ?? []).Select(s => new FederationSource(s.ConnectionId, s.Sql, s.Alias))],
        body.Sql ?? "", body.MaxRowsPerSource);

    private static object Wire(ResultChunk chunk) => chunk switch
    {
        ResultChunk.Columns c => new { type = "columns", statement = c.Statement, columns = c.Items },
        ResultChunk.Rows r => new { type = "rows", statement = r.Statement, rows = r.Items },
        ResultChunk.End e => new
        {
            type = "end", statement = e.Statement, rowsAffected = e.RowsAffected,
            elapsedMs = e.ElapsedMs, truncated = e.Truncated,
        },
        ResultChunk.Error x => new { type = "error", statement = x.Statement, text = x.Text },
        _ => new { type = "unknown" },
    };

    private static async Task WriteAsync(HttpContext ctx, object payload, CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, Json, ct);
        await ctx.Response.Body.WriteAsync("\n"u8.ToArray(), ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
