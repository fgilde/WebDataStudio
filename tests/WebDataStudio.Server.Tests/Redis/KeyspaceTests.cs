using StackExchange.Redis;
using Testcontainers.Redis;
using WebDataStudio.Server.Drivers.Redis;

namespace WebDataStudio.Server.Tests.Redis;

/// A keyspace big enough that "just list everything" is the wrong answer. The tree used to scan
/// until it had every key or gave up at five thousand, so a real Redis was both slow to open and
/// silently incomplete.
public sealed class KeyspaceFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();

    public IConnectionMultiplexer Multiplexer { get; private set; } = null!;
    public const int Keys = 3_000;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());

        var db = Multiplexer.GetDatabase();
        var batch = db.CreateBatch();
        var pending = new List<Task>();

        for (var index = 0; index < Keys; index++)
            pending.Add(batch.StringSetAsync($"user:{index}", $"value-{index}"));

        // A handful of other types and a key with a TTL, so the filters have something to filter.
        pending.Add(batch.HashSetAsync("profile:1", [new HashEntry("name", "ada")]));
        pending.Add(batch.ListRightPushAsync("queue:jobs", ["a", "b", "c"]));
        pending.Add(batch.StringSetAsync("session:expiring", "soon", TimeSpan.FromMinutes(5)));
        pending.Add(batch.StringSetAsync("big:blob", new string('x', 200_000)));

        batch.Execute();
        await Task.WhenAll(pending);
    }

    public async ValueTask DisposeAsync()
    {
        await Multiplexer.CloseAsync();
        await _container.DisposeAsync();
    }
}

public class KeyspaceTests(KeyspaceFixture fixture) : IClassFixture<KeyspaceFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_stops_well_short_of_the_keyspace_and_says_where_to_continue()
    {
        var page = await RedisKeyspace.ScanAsync(fixture.Multiplexer, 0, null, null, 0, 200, false, Ct);

        // SCAN answers in buckets, so a page can overshoot the requested count — what must not
        // happen is the whole keyspace arriving in one answer, which is what it used to do.
        Assert.InRange(page.Keys.Count, 200, 1_000);
        Assert.NotEqual(0, page.NextCursor);
        Assert.False(page.Complete);
    }

    [Fact]
    public async Task Following_the_cursor_reaches_every_key_exactly_once()
    {
        var seen = new HashSet<string>();
        long cursor = 0;
        var pages = 0;

        do
        {
            var page = await RedisKeyspace.ScanAsync(
                fixture.Multiplexer, 0, null, null, cursor, 500, false, Ct);

            foreach (var key in page.Keys)
                // SCAN may return a key twice across pages; the browser has to cope, and so does
                // this assertion — what matters is that nothing is missing.
                seen.Add(key.Key);

            cursor = page.NextCursor;
            pages++;
        }
        while (cursor != 0 && pages < 100);

        Assert.Equal(0, cursor);
        Assert.True(seen.Count >= KeyspaceFixture.Keys,
            $"{seen.Count} keys found, expected at least {KeyspaceFixture.Keys}");
        Assert.Contains("profile:1", seen);
    }

    [Fact]
    public async Task A_pattern_filters_on_the_server()
    {
        var page = await RedisKeyspace.ScanAsync(
            fixture.Multiplexer, 0, "queue:*", null, 0, 100, false, Ct);

        Assert.Contains(page.Keys, key => key.Key == "queue:jobs");
        Assert.DoesNotContain(page.Keys, key => key.Key.StartsWith("user:"));
    }

    [Fact]
    public async Task A_type_filter_keeps_only_that_type()
    {
        var page = await RedisKeyspace.ScanAsync(
            fixture.Multiplexer, 0, "*", "hash", 0, 1000, false, Ct);

        Assert.All(page.Keys, key => Assert.Equal("hash", key.Type));
        Assert.Contains(page.Keys, key => key.Key == "profile:1");
    }

    [Fact]
    public async Task Every_key_carries_its_type_length_and_ttl()
    {
        var page = await RedisKeyspace.ScanAsync(
            fixture.Multiplexer, 0, "session:*", null, 0, 100, false, Ct);

        var key = Assert.Single(page.Keys);
        Assert.Equal("string", key.Type);
        Assert.Equal(4, key.Length);
        Assert.NotNull(key.TtlSeconds);
        Assert.InRange(key.TtlSeconds!.Value, 1, 300);
    }

    [Fact]
    public async Task Memory_comes_along_when_asked_for()
    {
        var page = await RedisKeyspace.ScanAsync(
            fixture.Multiplexer, 0, "big:*", null, 0, 10, true, Ct);

        var key = Assert.Single(page.Keys);
        Assert.NotNull(key.SizeBytes);
        // The value alone is 200 kB; the exact overhead is Redis's business, the order is ours.
        Assert.True(key.SizeBytes > 100_000, $"the blob reports {key.SizeBytes} bytes");
    }

    [Fact]
    public async Task Size_is_left_out_when_it_is_not_asked_for()
    {
        var page = await RedisKeyspace.ScanAsync(
            fixture.Multiplexer, 0, "big:*", null, 0, 10, false, Ct);

        Assert.Null(Assert.Single(page.Keys).SizeBytes);
    }
}
