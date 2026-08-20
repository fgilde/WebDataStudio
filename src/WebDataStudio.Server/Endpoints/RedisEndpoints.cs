using Microsoft.AspNetCore.Mvc;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.Redis;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// Redis is not a SQL engine, and pretending otherwise is what makes a studio useless for it: keys
/// are browsed rather than queried, a value's type decides how it is edited, and half the useful
/// operations are administrative. These endpoints exist alongside the command console, not instead
/// of it.
///
/// Everything here refuses a connection that is not Redis, so a wrong id is a 400 rather than a
/// confusing cast error deep in a driver.
public static class RedisEndpoints
{
    public static void MapRedisEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/redis");

        api.MapGet("/{conn}/keys", async (
            string conn, int? db, string? match, string? type, long? cursor, int? count,
            bool? withSize, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    // A pooled or tunnelled session is a wrapper around the one the driver opened.
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    var page = await RedisKeyspace.ScanAsync(
                        redis.Multiplexer, db ?? redis.DatabaseNumber, match, type,
                        cursor ?? 0, count ?? 200, withSize ?? true, ct);

                    return Results.Ok(page);
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        api.MapGet("/{conn}/databases", async (
            string conn, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    // A pooled or tunnelled session is a wrapper around the one the driver opened.
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    // Sizes per database, so the browser can say which of the sixteen hold anything.
                    var counts = new List<object>();
                    for (var index = 0; index < Math.Max(redis.Server.DatabaseCount, 1); index++)
                    {
                        var size = await redis.Multiplexer.GetDatabase(index)
                            .ExecuteAsync("DBSIZE");
                        counts.Add(new { database = index, keys = (long)size });
                    }

                    return Results.Ok(counts);
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        static IResult NotRedis() =>
            Results.BadRequest(new { message = "this connection is not a Redis connection" });
    }
}
