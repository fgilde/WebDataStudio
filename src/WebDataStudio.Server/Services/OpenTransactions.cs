using System.Data.Common;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One transaction somebody opened and has not closed yet.
public sealed record OpenTransaction(
    string Id, string ConnectionId, DateTimeOffset Started, int Statements, string? LastStatement);

/// Transactions a person holds open across requests.
///
/// Auto-commit is the studio's default and stays it. This is the other mode: `BEGIN`, then run
/// statements, look at what they did, and only then commit — or roll the whole thing back. Every
/// tool that has this has it because "UPDATE without WHERE" is a thing people do at four in the
/// afternoon.
///
/// A held transaction keeps its session out of the pool for as long as it lives, so it is swept:
/// one that nobody touched for a while is rolled back rather than left holding locks.
public sealed class OpenTransactions : IAsyncDisposable
{
    private sealed class Held
    {
        public required string ConnectionId { get; init; }
        public required IDbDriver Driver { get; init; }
        public required IDbSession Session { get; init; }
        public required DbTransaction Transaction { get; init; }
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset Touched { get; set; } = DateTimeOffset.UtcNow;
        public int Statements { get; set; }
        public string? LastStatement { get; set; }
    }

    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Timer _sweeper;
    private readonly ILogger<OpenTransactions> _log;

    /// How long an untouched transaction may hold its locks before the studio rolls it back. A tab
    /// somebody closed, a browser that crashed, a laptop that went to sleep — all of them end here.
    public TimeSpan IdleTimeout { get; }

    public OpenTransactions(IConfiguration config, ILogger<OpenTransactions> log)
    {
        _log = log;
        IdleTimeout = TimeSpan.FromSeconds(
            int.TryParse(config["WDS_TRANSACTION_IDLE_SECONDS"], out var idle) && idle > 0 ? idle : 900);

        var interval = TimeSpan.FromSeconds(Math.Clamp(IdleTimeout.TotalSeconds / 4, 15, 120));
        _sweeper = new Timer(_ => Sweep(), null, interval, interval);
    }

    public async Task<OpenTransaction> BeginAsync(string connectionId, IDbDriver driver,
        IDbSession session, CancellationToken ct)
    {
        if (!driver.Caps.Transactions)
            throw new NotSupportedException($"{driver.Info.Label} has no transactions to hold open");

        var transaction = await session.Connection.BeginTransactionAsync(ct);

        // The driver runs its statements on this session; the ambient transaction is how it knows
        // to enlist them rather than opening one of its own.
        session.Unwrap().Ambient = transaction;

        var id = Guid.NewGuid().ToString("n");
        var held = new Held
        {
            ConnectionId = connectionId, Driver = driver, Session = session, Transaction = transaction,
        };

        lock (_gate) _held[id] = held;

        return new OpenTransaction(id, connectionId, held.Started, 0, null);
    }

    /// The session to run on, or null when this id is not one we hold. Touching it keeps the sweep
    /// away for another idle window.
    public (IDbDriver Driver, IDbSession Session)? Use(string id, string sql)
    {
        lock (_gate)
        {
            if (!_held.TryGetValue(id, out var held)) return null;

            held.Touched = DateTimeOffset.UtcNow;
            held.Statements++;
            held.LastStatement = sql.Length > 200 ? sql[..200] : sql;

            return (held.Driver, held.Session);
        }
    }

    public async Task<bool> CommitAsync(string id) => await CloseAsync(id, commit: true);

    public async Task<bool> RollbackAsync(string id) => await CloseAsync(id, commit: false);

    private async Task<bool> CloseAsync(string id, bool commit)
    {
        Held? held;
        lock (_gate)
        {
            if (!_held.Remove(id, out held)) return false;
        }

        try
        {
            if (commit) await held.Transaction.CommitAsync(CancellationToken.None);
            else await held.Transaction.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            // Whatever happened, the session goes back: the ambient transaction is cleared first so
            // the next borrower does not inherit a dead one.
            held.Session.Unwrap().Ambient = null;
            await held.Transaction.DisposeAsync();
            await held.Session.DisposeAsync();
        }

        return true;
    }

    /// What is open right now. The UI shows this so a transaction cannot be forgotten quietly, and
    /// the shell warns before a tab that holds one is closed.
    public IReadOnlyList<OpenTransaction> All()
    {
        lock (_gate)
            return _held
                .Select(entry => new OpenTransaction(entry.Key, entry.Value.ConnectionId,
                    entry.Value.Started, entry.Value.Statements, entry.Value.LastStatement))
                .ToList();
    }

    public OpenTransaction? Find(string id) => All().FirstOrDefault(one => one.Id == id);

    /// A transaction nobody touched for a whole idle window is rolled back. Holding locks on a
    /// production table because a browser tab was closed is worse than losing uncommitted work
    /// nobody came back for.
    private void Sweep()
    {
        var stale = new List<string>();

        lock (_gate)
            foreach (var (id, held) in _held)
                if (DateTimeOffset.UtcNow - held.Touched > IdleTimeout) stale.Add(id);

        foreach (var id in stale)
        {
            _log.LogWarning("rolling back transaction {Id}: nothing happened on it for {Idle}",
                id, IdleTimeout);

            _ = RollbackAsync(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sweeper.DisposeAsync();

        // Shutting down with work in flight is a rollback, never a commit: nobody said to keep it.
        foreach (var one in All()) await RollbackAsync(one.Id);
    }
}
