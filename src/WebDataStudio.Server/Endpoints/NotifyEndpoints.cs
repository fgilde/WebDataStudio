using System.Text.Json;
using System.Threading.Channels;
using Npgsql;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// PostgreSQL's own message bus.
///
/// `LISTEN` and `NOTIFY` are how a PostgreSQL application tells itself something happened — a job
/// queue woke up, a cache should drop a key, a trigger fired. Redis pub/sub already has a panel
/// here, and this is the same question for the other half of most stacks: *is anything actually
/// coming through?*
public static class NotifyEndpoints
{
    public record NotifyRequest(string Channel, string? Payload);

    /// A channel is an identifier, not a string literal, so it is quoted rather than parameterised —
    /// which also means `MyChannel` stays `MyChannel` instead of being folded to lower case.
    private static string Quoted(string channel) => "\"" + channel.Replace("\"", "\"\"") + "\"";

    public static void MapNotifyEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/notify");

        // Server-sent events, like the Redis subscription: the browser has EventSource built in,
        // and listening is one-directional by nature. It lives as long as the request does.
        api.MapGet("/{conn}/listen", async (string conn, string channels, HttpContext ctx,
            SessionFactory factory, CancellationToken ct) =>
        {
            // Its own connection, not one of the pool's: this one is parked in WaitAsync for as long
            // as the browser keeps the stream, and Npgsql leaves it in a state the pool cannot hand
            // on to anybody else. See SessionFactory.OpenExclusiveAsync.
            var (driver, session) = await factory.OpenExclusiveAsync(conn, ct);
            await using (session)
            {
                if (session.Connection is not NpgsqlConnection connection)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        message = $"{driver.Info.Label} has no LISTEN/NOTIFY",
                    }, ct);
                    return;
                }

                var names = channels.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (names.Length == 0)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new { message = "name a channel to listen on" }, ct);
                    return;
                }

                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";

                var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1_000)
                {
                    // A browser that cannot keep up loses the oldest messages rather than holding
                    // the whole firehose in memory.
                    FullMode = BoundedChannelFullMode.DropOldest,
                });

                void OnNotice(object _, NpgsqlNotificationEventArgs e) =>
                    queue.Writer.TryWrite(JsonSerializer.Serialize(new
                    {
                        channel = e.Channel,
                        message = e.Payload,
                        // Which backend sent it: the answer to "was that me, or the application?"
                        pid = e.PID,
                        at = DateTimeOffset.UtcNow,
                    }));

                connection.Notification += OnNotice;

                // A stop of this listener's own, linked to the request's. The waiter has to be
                // brought back before the connection is closed — a connector still inside WaitAsync
                // throws NpgsqlOperationInProgressException when it is disposed — and the stream can
                // also end without the request being cancelled at all.
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task waiting = Task.CompletedTask;

                try
                {
                    foreach (var name in names)
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = $"LISTEN {Quoted(name)}";
                        await command.ExecuteNonQueryAsync(ct);
                    }

                    // Two halves: one waits on the socket so Npgsql can raise the event, the other
                    // writes what the event queued. WaitAsync is the only way to hear a
                    // notification that arrives while nothing else is running on the connection.
                    waiting = Task.Run(async () =>
                    {
                        try
                        {
                            while (!stop.IsCancellationRequested) await connection.WaitAsync(stop.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // The stream ended. Swallowed here rather than left for the task to
                            // carry: nobody awaits this one, and an unobserved exception at shutdown
                            // is a crash with a stack trace about something that went to plan.
                        }
                        catch (NpgsqlException)
                        {
                            // The connection went away while it was waiting, which is the same thing
                            // from the other side.
                        }
                    }, ct);

                    // A comment, flushed straight away: until something is written the response
                    // headers have not left, and a browser — or a test — waiting for them would
                    // be waiting for the notification it is supposed to trigger.
                    await ctx.Response.WriteAsync(": listening\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);

                    while (!ct.IsCancellationRequested)
                    {
                        var next = queue.Reader.WaitToReadAsync(ct).AsTask();

                        // A channel can be quiet for hours and still be the right channel, and
                        // a proxy in front of the studio tends to read a quiet stream as a dead
                        // one. A comment now and then keeps it open and costs nothing.
                        if (await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(25), ct)) != next)
                        {
                            await ctx.Response.WriteAsync(": still here\n\n", ct);
                            await ctx.Response.Body.FlushAsync(ct);
                            continue;
                        }

                        if (!await next) break;

                        while (queue.Reader.TryRead(out var payload))
                        {
                            await ctx.Response.WriteAsync($"data: {payload}\n\n", ct);
                            await ctx.Response.Body.FlushAsync(ct);
                        }
                    }

                }
                catch (OperationCanceledException)
                {
                    // The client went away, which is how listening ends.
                }
                finally
                {
                    // In this order: stop waiting, wait for the waiter to be out, then let go of the
                    // event. Closing the connection is what comes after this block, and it needs the
                    // connector idle.
                    await stop.CancelAsync();

                    try { await waiting; }
                    catch (Exception) { /* it ended, which is all this needed */ }

                    connection.Notification -= OnNotice;
                }
            }
        });

        // Sending one, for trying the other end without leaving the studio.
        api.MapPost("/{conn}/send", async (string conn, NotifyRequest body, SessionFactory factory,
            CancellationToken ct) =>
        {
            if (body.Channel is not { Length: > 0 })
                return Results.BadRequest(new { message = "name a channel to send on" });

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Connection is not NpgsqlConnection connection)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} has no LISTEN/NOTIFY",
                        });

                    if (session.Spec.ReadOnly)
                        return Results.Json(new { message = "this connection is read-only" },
                            statusCode: StatusCodes.Status403Forbidden);

                    // The channel is an identifier and the payload is a value: quoted and
                    // parameterised respectively, which is why this uses pg_notify rather than
                    // building a NOTIFY statement around somebody's text.
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT pg_notify($1, $2)";
                    command.Parameters.Add(new NpgsqlParameter { Value = body.Channel });
                    command.Parameters.Add(new NpgsqlParameter { Value = body.Payload ?? "" });
                    await command.ExecuteNonQueryAsync(ct);

                    return Results.NoContent();
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }
}
