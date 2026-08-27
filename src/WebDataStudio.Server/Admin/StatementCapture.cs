using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Admin;

/// One statement seen during a capture, and what was seen of it.
public sealed record CapturedStatement(
    string Text,
    int Samples,
    long MaxDurationMs,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyList<string> Sessions,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Databases,
    bool Blocked);

public sealed record CaptureState(
    string State,
    DateTimeOffset? StartedAt,
    int Seconds,
    int SecondsLeft,
    int Samples,
    IReadOnlyList<CapturedStatement> Statements,
    string? Error);

/// "Show me what runs on this server in the next minute."
///
/// Extended Events and its equivalents are the real answer to that question, and they need
/// permissions and a place to put a session that a studio has no business arranging. This is the
/// small version of it: the server's own list of what it is doing, read once a second for as long as
/// somebody asked, and grouped by statement. A statement that ran and finished between two samples
/// is missed — which is the honest limit of sampling, and the panel says so.
///
/// The same code covers every engine that can say what it is running: SQL Server, PostgreSQL and
/// MySQL all answer the question the session list already asks.
public sealed class StatementCapture(SessionFactory factory, ILogger<StatementCapture> log)
{
    /// Long enough to catch the thing somebody is chasing, short enough that a forgotten capture is
    /// not a session held open all afternoon.
    public const int MaxSeconds = 300;

    private sealed record Running(
        CaptureState State,
        Dictionary<string, CapturedStatement> Seen,
        CancellationTokenSource Cancel);

    private readonly ConcurrentDictionary<string, Running> _captures = new();

    public CaptureState Start(string connectionId, int seconds)
    {
        var window = Math.Clamp(seconds, 1, MaxSeconds);

        if (_captures.TryGetValue(connectionId, out var existing)
            && existing.State.State == "running")
            return existing.State;

        var cancel = new CancellationTokenSource();
        var started = DateTimeOffset.UtcNow;

        _captures[connectionId] = new Running(
            new CaptureState("running", started, window, window, 0, [], null), [], cancel);

        // Not awaited: the capture outlives the request that asked for it, and the browser polls.
        _ = Task.Run(() => RunAsync(connectionId, window, started, cancel.Token), CancellationToken.None);

        return _captures[connectionId].State;
    }

    private async Task RunAsync(
        string connectionId, int seconds, DateTimeOffset started, CancellationToken ct)
    {
        try
        {
            var (driver, session) = await factory.OpenAsync(connectionId, ct);

            await using (session)
            {
                if (!driver.Caps.SessionList)
                {
                    Fail(connectionId, $"{driver.Info.Label} cannot say what it is running");
                    return;
                }

                var samples = 0;

                while (!ct.IsCancellationRequested
                       && DateTimeOffset.UtcNow < started.AddSeconds(seconds))
                {
                    // The same list the sessions tab reads. One statement per session, which is what
                    // the server itself knows — no tracing, no permissions beyond that list.
                    foreach (var entry in await SessionService.ListAsync(driver, session, ct))
                        Record(connectionId, entry);

                    samples++;
                    Advance(connectionId, samples, started, seconds);

                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }

                Finish(connectionId, samples, started, seconds);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose: whatever was seen so far stays readable.
            if (_captures.TryGetValue(connectionId, out var stopped))
                _captures[connectionId] = stopped with
                {
                    State = stopped.State with { State = "stopped", SecondsLeft = 0 },
                };
        }
        catch (Exception e)
        {
            log.LogWarning("capture on {Connection} failed: {Message}", connectionId, e.Message);
            Fail(connectionId, e.Message);
        }
    }

    private void Record(string connectionId, SessionEntry entry)
    {
        if (entry.Query.Trim().Length == 0) return;
        if (!_captures.TryGetValue(connectionId, out var running)) return;

        var text = Normalise(entry.Query);
        var now = DateTimeOffset.UtcNow;

        lock (running.Seen)
        {
            if (running.Seen.TryGetValue(text, out var seen))
                running.Seen[text] = seen with
                {
                    Samples = seen.Samples + 1,
                    MaxDurationMs = Math.Max(seen.MaxDurationMs, entry.DurationMs),
                    LastSeen = now,
                    Sessions = seen.Sessions.Contains(entry.Id)
                        ? seen.Sessions
                        : [.. seen.Sessions, entry.Id],
                    Users = seen.Users.Contains(entry.User) || entry.User.Length == 0
                        ? seen.Users
                        : [.. seen.Users, entry.User],
                    Databases = seen.Databases.Contains(entry.Database) || entry.Database.Length == 0
                        ? seen.Databases
                        : [.. seen.Databases, entry.Database],
                    Blocked = seen.Blocked || entry.BlockedBy is { Length: > 0 },
                };
            else
                running.Seen[text] = new CapturedStatement(text, 1, entry.DurationMs, now, now,
                    [entry.Id],
                    entry.User.Length > 0 ? [entry.User] : [],
                    entry.Database.Length > 0 ? [entry.Database] : [],
                    entry.BlockedBy is { Length: > 0 });
        }
    }

    /// The studio's own polling of the session list would otherwise dominate every capture.
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string Normalise(string query) => Whitespace.Replace(query.Trim(), " ");

    private void Advance(string connectionId, int samples, DateTimeOffset started, int seconds)
    {
        if (!_captures.TryGetValue(connectionId, out var running)) return;

        var left = (int)Math.Max(0, (started.AddSeconds(seconds) - DateTimeOffset.UtcNow).TotalSeconds);

        _captures[connectionId] = running with
        {
            State = running.State with { Samples = samples, SecondsLeft = left, Statements = Snapshot(running) },
        };
    }

    private void Finish(string connectionId, int samples, DateTimeOffset started, int seconds)
    {
        if (!_captures.TryGetValue(connectionId, out var running)) return;

        _captures[connectionId] = running with
        {
            State = running.State with
            {
                State = "done", Samples = samples, SecondsLeft = 0, Statements = Snapshot(running),
            },
        };
    }

    private void Fail(string connectionId, string message)
    {
        if (!_captures.TryGetValue(connectionId, out var running)) return;

        _captures[connectionId] = running with
        {
            State = running.State with { State = "failed", SecondsLeft = 0, Error = message },
        };
    }

    private static IReadOnlyList<CapturedStatement> Snapshot(Running running)
    {
        lock (running.Seen)
            // The longest first: the reason for looking is usually the slow one.
            return running.Seen.Values
                .OrderByDescending(statement => statement.MaxDurationMs)
                .ToList();
    }

    public CaptureState Status(string connectionId) =>
        _captures.TryGetValue(connectionId, out var running)
            ? running.State
            : new CaptureState("none", null, 0, 0, 0, [], null);

    public void Stop(string connectionId)
    {
        if (_captures.TryGetValue(connectionId, out var running)) running.Cancel.Cancel();
    }
}
