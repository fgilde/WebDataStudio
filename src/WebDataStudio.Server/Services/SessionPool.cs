using System.Data;
using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// A session handed out by the pool. Disposing it returns it instead of closing it, so the
/// endpoints keep their `await using` and know nothing about pooling.
internal sealed class PooledSession(IDbSession inner, SessionPool pool, string connectionId) : IDbSessionWrapper
{
    private bool _returned;

    public IDbSession Inner => inner;

    public ConnectionSpec Spec => inner.Spec;
    public DbConnection Connection => inner.Connection;

    public async ValueTask DisposeAsync()
    {
        if (_returned) return;
        _returned = true;
        await pool.ReturnAsync(connectionId, inner);
    }
}

/// Keeps open sessions around between requests and caps how many a single connection may hold.
/// The ADO providers pool their own connections underneath; this layer exists for the cap and for
/// the engines whose clients do not pool at all.
/// Every session this connection allows is in use, and none came free.
///
/// Usually somebody is running something long on the same connection. Where it is not, it is a
/// session that was borrowed and never given back — which is worth saying plainly, because the
/// symptom on its own is a studio that appears to have frozen.
public sealed class SessionsBusyException(int limit, TimeSpan waited)
    : Exception($"all {limit} session(s) this connection allows are in use, and none came free "
                + $"within {waited.TotalSeconds:0} seconds. Something long may still be running on "
                + "it; raise WDS_MAX_SESSIONS if this connection needs more at once.");

public sealed class SessionPool : IAsyncDisposable
{
    private sealed record Idle(IDbSession Session, DateTimeOffset Since);

    private readonly Dictionary<string, Stack<Idle>> _idle = new();
    private readonly Dictionary<string, SemaphoreSlim> _slots = new();
    private readonly Lock _gate = new();
    private readonly Timer _sweeper;

    public int MaxSessions { get; }
    public TimeSpan IdleTimeout { get; }

    public SessionPool(IConfiguration config)
    {
        MaxSessions = int.TryParse(config["WDS_MAX_SESSIONS"], out var max) && max > 0 ? max : 8;
        IdleTimeout = TimeSpan.FromSeconds(
            int.TryParse(config["WDS_IDLE_TIMEOUT_SECONDS"], out var idle) && idle > 0 ? idle : 300);

        var interval = TimeSpan.FromSeconds(Math.Clamp(IdleTimeout.TotalSeconds / 4, 5, 60));
        _sweeper = new Timer(_ => Sweep(), null, interval, interval);
    }

    /// How long a request waits for a session before the studio says what is going on.
    ///
    /// A wait is normal — somebody's report is running and this connection allows four at a time.
    /// An endless wait is not: it looks exactly like a studio that has frozen, and a browser only
    /// gives one host six connections at once, so a handful of them takes the whole window down
    /// with them. After this, the answer is a sentence.
    public static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(30);

    /// Waits for a slot, then reuses an idle session or opens a fresh one through `open`.
    public async Task<IDbSession> RentAsync(string connectionId, Func<CancellationToken, Task<IDbSession>> open,
        CancellationToken ct)
    {
        if (!await Slot(connectionId).WaitAsync(SlotWait, ct))
            throw new SessionsBusyException(MaxSessions, SlotWait);

        try
        {
            while (true)
            {
                IDbSession? candidate = null;

                lock (_gate)
                    if (_idle.TryGetValue(connectionId, out var stack) && stack.Count > 0)
                        candidate = stack.Pop().Session;

                if (candidate is null) break;

                // An idle session can have been closed by the server in the meantime.
                if (Usable(candidate)) return new PooledSession(candidate, this, connectionId);
                await candidate.DisposeAsync();
            }

            return new PooledSession(await open(ct), this, connectionId);
        }
        catch (Exception)
        {
            Slot(connectionId).Release();
            throw;
        }
    }

    internal async ValueTask ReturnAsync(string connectionId, IDbSession session)
    {
        var keep = Usable(session);

        if (keep)
            lock (_gate)
            {
                if (!_idle.TryGetValue(connectionId, out var stack))
                    _idle[connectionId] = stack = new Stack<Idle>();
                stack.Push(new Idle(session, DateTimeOffset.UtcNow));
            }
        else
            await session.DisposeAsync();

        Slot(connectionId).Release();
    }

    /// Drops every idle session for one connection — used when its definition changes, because
    /// the pooled ones still point at the old target.
    public async Task EvictAsync(string connectionId)
    {
        List<Idle> dropped;

        lock (_gate)
        {
            if (!_idle.TryGetValue(connectionId, out var stack)) return;
            dropped = [.. stack];
            stack.Clear();
        }

        foreach (var entry in dropped) await entry.Session.DisposeAsync();
    }

    public int IdleCount(string connectionId)
    {
        lock (_gate) return _idle.TryGetValue(connectionId, out var stack) ? stack.Count : 0;
    }

    private static bool Usable(IDbSession session)
    {
        try
        {
            // A broken or half-open connection is worse than opening a new one.
            return session.Connection.State == ConnectionState.Open;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private SemaphoreSlim Slot(string connectionId)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(connectionId, out var slot))
                _slots[connectionId] = slot = new SemaphoreSlim(MaxSessions, MaxSessions);
            return slot;
        }
    }

    /// Drops every session idle for longer than the timeout. Public so a test can run it on
    /// demand rather than sleeping out the timer interval.
    public void Sweep()
    {
        var expired = new List<IDbSession>();
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;

        lock (_gate)
            foreach (var stack in _idle.Values)
            {
                var keep = stack.Where(e => e.Since > cutoff).Reverse().ToList();
                expired.AddRange(stack.Where(e => e.Since <= cutoff).Select(e => e.Session));

                stack.Clear();
                foreach (var entry in keep) stack.Push(entry);
            }

        foreach (var session in expired) _ = session.DisposeAsync().AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        await _sweeper.DisposeAsync();

        List<IDbSession> all;
        lock (_gate)
        {
            all = _idle.Values.SelectMany(s => s).Select(e => e.Session).ToList();
            _idle.Clear();
        }

        foreach (var session in all) await session.DisposeAsync();
    }
}
