using StackExchange.Redis;
using Testcontainers.Redis;
using WebDataStudio.Server.Drivers.Redis;

namespace WebDataStudio.Server.Tests.Redis;

/// "Delete every session:* key" is a normal day's work with Redis and cannot be undone, so it
/// happens in two steps: match, then apply that matched set. Re-scanning at apply time would mean
/// the set that was approved and the set that is deleted are two different things.
public class BulkTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private IConnectionMultiplexer _multiplexer = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());

        var db = _multiplexer.GetDatabase();
        for (var index = 0; index < 40; index++) await db.StringSetAsync($"session:{index}", "token");
        for (var index = 0; index < 10; index++) await db.StringSetAsync($"order:{index}", "keep me");
        await db.HashSetAsync("session:hash", [new HashEntry("kind", "hash")]);
    }

    public async ValueTask DisposeAsync()
    {
        await _multiplexer.CloseAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Matching_counts_the_keys_and_touches_nothing()
    {
        var matched = await RedisBulk.MatchAsync(_multiplexer, 0, "session:*", null, Ct);

        Assert.Equal(41, matched.Count);
        Assert.Equal(51L, (long)await _multiplexer.GetDatabase().ExecuteAsync("DBSIZE"));
    }

    [Fact]
    public async Task Matching_can_be_narrowed_to_a_type()
    {
        var matched = await RedisBulk.MatchAsync(_multiplexer, 0, "session:*", "hash", Ct);

        Assert.Equal(["session:hash"], matched);
    }

    [Fact]
    public async Task The_matched_set_has_no_duplicates_even_though_scan_may_repeat_a_key()
    {
        var matched = await RedisBulk.MatchAsync(_multiplexer, 0, "*", null, Ct);

        Assert.Equal(matched.Count, matched.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Deleting_removes_exactly_what_matched()
    {
        var db = _multiplexer.GetDatabase();
        for (var index = 0; index < 5; index++) await db.StringSetAsync($"doomed:{index}", "x");

        var matched = await RedisBulk.MatchAsync(_multiplexer, 0, "doomed:*", null, Ct);
        var affected = await RedisBulk.ApplyAsync(
            _multiplexer, new BulkRequest(0, "doomed:*", null, "delete", null), matched, Ct);

        Assert.Equal(5, affected);
        Assert.False(await db.KeyExistsAsync("doomed:0"));
        // And nothing else: this is the assertion that matters for a delete by pattern.
        Assert.True(await db.KeyExistsAsync("order:0"));
    }

    [Fact]
    public async Task Expiring_sets_a_ttl_on_the_matched_keys_only()
    {
        var db = _multiplexer.GetDatabase();
        for (var index = 0; index < 3; index++) await db.StringSetAsync($"aging:{index}", "x");

        var matched = await RedisBulk.MatchAsync(_multiplexer, 0, "aging:*", null, Ct);
        var affected = await RedisBulk.ApplyAsync(
            _multiplexer, new BulkRequest(0, "aging:*", null, "expire", 300), matched, Ct);

        Assert.Equal(3, affected);
        Assert.NotNull(await db.KeyTimeToLiveAsync("aging:0"));
        Assert.Null(await db.KeyTimeToLiveAsync("order:0"));
    }

    [Fact]
    public async Task Persisting_takes_the_ttl_back_off()
    {
        var db = _multiplexer.GetDatabase();
        await db.StringSetAsync("kept:1", "x", TimeSpan.FromMinutes(5));

        var matched = await RedisBulk.MatchAsync(_multiplexer, 0, "kept:*", null, Ct);
        await RedisBulk.ApplyAsync(
            _multiplexer, new BulkRequest(0, "kept:*", null, "persist", null), matched, Ct);

        Assert.Null(await db.KeyTimeToLiveAsync("kept:1"));
    }

    [Fact]
    public async Task An_unknown_action_is_refused_rather_than_guessed()
    {
        var matched = await RedisBulk.MatchAsync(_multiplexer, 0, "order:*", null, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => RedisBulk.ApplyAsync(
            _multiplexer, new BulkRequest(0, "order:*", null, "incinerate", null), matched, Ct));
    }

    // The set is resolved once and applied as it was: a key created between the two steps is not
    // part of what was approved, and must survive.
    [Fact]
    public async Task A_key_created_after_the_match_is_not_deleted()
    {
        var db = _multiplexer.GetDatabase();
        await db.StringSetAsync("late:1", "x");

        var matched = await RedisBulk.MatchAsync(_multiplexer, 0, "late:*", null, Ct);
        await db.StringSetAsync("late:2", "created after the preview");

        await RedisBulk.ApplyAsync(
            _multiplexer, new BulkRequest(0, "late:*", null, "delete", null), matched, Ct);

        Assert.False(await db.KeyExistsAsync("late:1"));
        Assert.True(await db.KeyExistsAsync("late:2"));
    }
}
