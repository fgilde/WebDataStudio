using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class SessionPoolTests
{
    private sealed class FakeSession : IDbSession
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public FakeSession() => _connection.Open();

        public bool Disposed { get; private set; }
        public ConnectionSpec Spec { get; } =
            new("c", "c", "sqlite", "Data Source=:memory:", false, null, null, ConnectionSource.Stored);
        public DbConnection Connection => _connection;

        public void Break() => _connection.Close();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _connection.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static SessionPool Pool(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    private static Func<CancellationToken, Task<IDbSession>> Opener(List<FakeSession> created) =>
        _ =>
        {
            var session = new FakeSession();
            created.Add(session);
            return Task.FromResult<IDbSession>(session);
        };

    [Fact]
    public async Task Two_concurrent_rents_get_two_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<FakeSession>();
        await using var pool = Pool();

        var first = await pool.RentAsync("c", Opener(created), ct);
        var second = await pool.RentAsync("c", Opener(created), ct);

        Assert.NotSame(first.Connection, second.Connection);
        Assert.Equal(2, created.Count);
    }

    [Fact]
    public async Task A_returned_session_is_reused()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<FakeSession>();
        await using var pool = Pool();

        var first = await pool.RentAsync("c", Opener(created), ct);
        var connection = first.Connection;
        await first.DisposeAsync();

        Assert.Equal(1, pool.IdleCount("c"));

        var second = await pool.RentAsync("c", Opener(created), ct);
        Assert.Same(connection, second.Connection);
        Assert.Single(created);
    }

    [Fact]
    public async Task An_idle_session_past_the_timeout_is_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<FakeSession>();
        await using var pool = Pool(("WDS_IDLE_TIMEOUT_SECONDS", "1"));

        await (await pool.RentAsync("c", Opener(created), ct)).DisposeAsync();
        await Task.Delay(1200, ct);
        pool.Sweep();

        Assert.Equal(0, pool.IdleCount("c"));
    }

    [Fact]
    public async Task The_cap_blocks_the_next_rent_until_one_comes_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<FakeSession>();
        await using var pool = Pool(("WDS_MAX_SESSIONS", "2"));

        var first = await pool.RentAsync("c", Opener(created), ct);
        var second = await pool.RentAsync("c", Opener(created), ct);

        var third = pool.RentAsync("c", Opener(created), ct);
        Assert.False(third.IsCompleted);

        await second.DisposeAsync();
        Assert.Same(second.Connection, (await third).Connection);

        await first.DisposeAsync();
        await (await third).DisposeAsync();
    }

    [Fact]
    public async Task The_cap_is_per_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<FakeSession>();
        await using var pool = Pool(("WDS_MAX_SESSIONS", "1"));

        var first = await pool.RentAsync("a", Opener(created), ct);
        var other = await pool.RentAsync("b", Opener(created), ct);

        Assert.NotNull(first);
        Assert.NotNull(other);
    }

    [Fact]
    public async Task A_broken_session_is_discarded_rather_than_pooled()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<FakeSession>();
        await using var pool = Pool();

        var session = await pool.RentAsync("c", Opener(created), ct);
        created[0].Break();
        await session.DisposeAsync();

        Assert.Equal(0, pool.IdleCount("c"));
        Assert.True(created[0].Disposed);
    }

    [Fact]
    public async Task Eviction_drops_the_idle_sessions_of_one_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<FakeSession>();
        await using var pool = Pool();

        await (await pool.RentAsync("a", Opener(created), ct)).DisposeAsync();
        await (await pool.RentAsync("b", Opener(created), ct)).DisposeAsync();

        await pool.EvictAsync("a");

        Assert.Equal(0, pool.IdleCount("a"));
        Assert.Equal(1, pool.IdleCount("b"));
    }

    [Fact]
    public async Task Disposing_the_pool_closes_everything()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<FakeSession>();
        var pool = Pool();

        await (await pool.RentAsync("c", Opener(created), ct)).DisposeAsync();
        await pool.DisposeAsync();

        Assert.True(created[0].Disposed);
    }

    [Fact]
    public async Task A_failed_open_frees_its_slot_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var pool = Pool(("WDS_MAX_SESSIONS", "1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pool.RentAsync("c", _ => throw new InvalidOperationException("no"), ct));

        // Without the slot coming back, the connection would be unusable for the rest of the run.
        var created = new List<FakeSession>();
        var session = await pool.RentAsync("c", Opener(created), ct);
        Assert.NotNull(session);
    }
}

public class SessionWrapperTests
{
    private sealed class DriverOwnSession : IDbSession
    {
        public ConnectionSpec Spec { get; } =
            new("c", "c", "redis", "localhost:6379", false, null, null, ConnectionSource.Stored);

        // A driver that is not ADO has no DbConnection to hand out.
        public DbConnection Connection => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Wrapper(IDbSession inner) : IDbSessionWrapper
    {
        public IDbSession Inner => inner;
        public ConnectionSpec Spec => inner.Spec;
        public DbConnection Connection => inner.Connection;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void An_unwrapped_session_is_itself()
    {
        var session = new DriverOwnSession();
        Assert.Same(session, session.Unwrap());
    }

    [Fact]
    public void Unwrap_looks_through_every_layer()
    {
        // The pool wraps what the tunnel already wrapped; a driver has to find its own session
        // under both, or MongoDB and Redis stop working the moment either feature is used.
        var session = new DriverOwnSession();
        var wrapped = new Wrapper(new Wrapper(session));

        Assert.Same(session, wrapped.Unwrap());
    }

    [Fact]
    public async Task A_pooled_session_reports_the_session_the_driver_created()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var pool = new SessionPool(new ConfigurationBuilder().Build());

        var inner = new DriverOwnSession();
        var rented = await pool.RentAsync("c", _ => Task.FromResult<IDbSession>(inner), ct);

        Assert.Same(inner, rented.Unwrap());
    }
}
