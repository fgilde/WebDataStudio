using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
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
    /// The hash a preview handed out, exactly as the data and DDL endpoints do it.
    public record ApplyRequest(string Hash);

    public record PublishRequest(string Channel, string Message);


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

        // --- one key --------------------------------------------------------------------------
        api.MapGet("/{conn}/value", async (
            string conn, int? db, string key, long? offset, int? count,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    var database = redis.Multiplexer.GetDatabase(db ?? redis.DatabaseNumber);
                    var value = await RedisValues.ReadAsync(
                        database, key, offset ?? 0, count ?? RedisValues.PageSize, ct);

                    return value is null
                        ? Results.NotFound(new { message = $"'{key}' does not exist" })
                        : Results.Ok(value);
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // Writing is two calls, like every other write in this studio: the first says what would
        // happen, the second runs exactly that. Redis has no transaction to undo afterwards, which
        // makes the preview the last place a mistake can be caught.
        api.MapPost("/{conn}/value/preview", async (
            string conn, ValueEditRequest body, SessionFactory factory, IMemoryCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();
                    if (session.Spec.ReadOnly) return ReadOnly();

                    var commands = RedisValues.Plan(body);
                    var hash = RedisValues.HashOf(commands);

                    cache.Set($"redis:{hash}", (body.Database, commands), TimeSpan.FromMinutes(10));

                    return Results.Ok(new ValuePreviewDto(
                        hash, commands, RedisValues.IsDestructive(body.Operation)));
                }
            }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        api.MapPost("/{conn}/value/apply", async (
            string conn, ApplyRequest body, SessionFactory factory, IMemoryCache cache,
            CancellationToken ct) =>
        {
            if (!cache.TryGetValue($"redis:{body.Hash}", out (int Database, IReadOnlyList<string> Commands) planned))
                return Results.BadRequest(new { message = "this change was not previewed, or the preview expired" });

            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();
                    if (session.Spec.ReadOnly) return ReadOnly();

                    var database = redis.Multiplexer.GetDatabase(planned.Database);
                    var executed = await RedisValues.ApplyAsync(database, planned.Database, planned.Commands, ct);

                    cache.Remove($"redis:{body.Hash}");
                    return Results.Ok(new { executed });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // --- what this server can do, and how it is put together ------------------------------
        // Straight from the server rather than from a list baked into the studio: a server with
        // modules has commands no such list knows about. Cached per connection because the answer
        // only changes when the server does.
        api.MapGet("/{conn}/commands", async (
            string conn, SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            if (cache.Get($"redis-commands:{conn}") is IReadOnlyList<CommandDoc> cached)
                return Results.Ok(new { commands = cached });

            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    var commands = await RedisCommandDocs.ListAsync(redis);
                    cache.Set($"redis-commands:{conn}", commands, TimeSpan.FromHours(1));

                    return Results.Ok(new { commands });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        api.MapGet("/{conn}/cluster", async (string conn, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    return Results.Ok(await RedisCommandDocs.DescribeAsync(redis));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // --- many keys at once ----------------------------------------------------------------
        api.MapPost("/{conn}/bulk/preview", async (
            string conn, BulkRequest body, SessionFactory factory, IMemoryCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();
                    if (session.Spec.ReadOnly) return ReadOnly();
                    if (string.IsNullOrWhiteSpace(body.Match))
                        return Results.BadRequest(new { message = "a pattern is required; '*' would be everything" });

                    var matched = await RedisBulk.MatchAsync(
                        redis.Multiplexer, body.Database, body.Match, body.Type, ct);

                    var hash = RedisValues.HashOf([body.Action, body.Match, body.Type ?? "", $"{body.TtlSeconds}", $"{matched.Count}"]);
                    cache.Set($"redis-bulk:{hash}", (body, matched), TimeSpan.FromMinutes(10));

                    return Results.Ok(new BulkPreviewDto(hash, matched.Count, matched.Take(20).ToList()));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        api.MapPost("/{conn}/bulk/apply", async (
            string conn, ApplyRequest body, SessionFactory factory, IMemoryCache cache,
            CancellationToken ct) =>
        {
            if (!cache.TryGetValue($"redis-bulk:{body.Hash}", out (BulkRequest Request, List<string> Keys) planned))
                return Results.BadRequest(new { message = "this change was not previewed, or the preview expired" });

            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();
                    if (session.Spec.ReadOnly) return ReadOnly();

                    var affected = await RedisBulk.ApplyAsync(
                        redis.Multiplexer, planned.Request, planned.Keys, ct);

                    cache.Remove($"redis-bulk:{body.Hash}");
                    return Results.Ok(new { affected });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // --- what the keyspace is made of ------------------------------------------------------
        api.MapGet("/{conn}/analysis", async (
            string conn, int? db, int? sample, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    return Results.Ok(await RedisAnalysis.RunAsync(
                        redis.Multiplexer, db ?? redis.DatabaseNumber,
                        sample ?? RedisAnalysis.DefaultSample, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // --- streams, the slow log, and who is connected ---------------------------------------
        api.MapGet("/{conn}/stream", async (
            string conn, int? db, string key, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    var database = redis.Multiplexer.GetDatabase(db ?? redis.DatabaseNumber);
                    return Results.Ok(await RedisOperations.StreamAsync(database, key, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (RedisServerException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        api.MapGet("/{conn}/slowlog", async (
            string conn, int? count, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    return Results.Ok(await RedisOperations.SlowLogAsync(redis.Server, count ?? 50, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        api.MapGet("/{conn}/clients", async (
            string conn, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();

                    return Results.Ok(await RedisOperations.ClientsAsync(redis.Server, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // --- pub/sub ---------------------------------------------------------------------------
        // Server-sent events rather than a socket: the browser has EventSource built in, and a
        // subscription is one-directional by nature. It lives as long as the request does.
        api.MapGet("/{conn}/subscribe", async (
            string conn, string channels, HttpContext ctx, SessionFactory factory,
            CancellationToken ct) =>
        {
            var (_, session) = await factory.OpenAsync(conn, ct);
            await using (session)
            {
                if (session.Unwrap() is not RedisSession redis)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";

                var subscriber = redis.Multiplexer.GetSubscriber();
                var queue = System.Threading.Channels.Channel.CreateBounded<string>(
                    new System.Threading.Channels.BoundedChannelOptions(1_000)
                    {
                        // A browser that cannot keep up loses the oldest messages rather than
                        // holding the whole firehose in memory.
                        FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                    });

                var patterns = channels.Split(',', StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries);

                foreach (var pattern in patterns)
                    await subscriber.SubscribeAsync(RedisChannel.Pattern(pattern), (channel, message) =>
                        queue.Writer.TryWrite(System.Text.Json.JsonSerializer.Serialize(new
                        {
                            channel = channel.ToString(),
                            message = message.ToString(),
                            at = DateTimeOffset.UtcNow,
                        })));

                try
                {
                    await foreach (var payload in queue.Reader.ReadAllAsync(ct))
                    {
                        await ctx.Response.WriteAsync($"data: {payload}\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The client went away, which is how a subscription ends.
                }
                finally
                {
                    foreach (var pattern in patterns)
                        await subscriber.UnsubscribeAsync(RedisChannel.Pattern(pattern));
                }
            }
        });

        api.MapPost("/{conn}/publish", async (
            string conn, PublishRequest body, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Unwrap() is not RedisSession redis) return NotRedis();
                    if (session.Spec.ReadOnly) return ReadOnly();

                    var receivers = await redis.Multiplexer.GetSubscriber()
                        .PublishAsync(RedisChannel.Literal(body.Channel), body.Message);

                    return Results.Ok(new { receivers });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        static IResult ReadOnly() => Results.Json(
            new { message = "this connection is read-only; nothing was changed" }, statusCode: 403);

        static IResult NotRedis() =>
            Results.BadRequest(new { message = "this connection is not a Redis connection" });
    }
}
