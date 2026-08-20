using System.Text.Json;
using StackExchange.Redis;
using Testcontainers.Redis;
using WebDataStudio.Server.Drivers.Redis;

namespace WebDataStudio.Server.Tests.Redis;

/// Reading and writing one key. The shape a value comes back in is the whole point: a hash is an
/// object, a list is an array, a sorted set carries scores. Flattening them into one shape is what
/// makes a Redis client feel like a spreadsheet with the wrong columns.
public sealed class ValueFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();

    public IConnectionMultiplexer Multiplexer { get; private set; } = null!;
    public IDatabase Db => Multiplexer.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());

        await Db.StringSetAsync("greeting", "hello");
        await Db.HashSetAsync("profile:1", [new HashEntry("name", "ada"), new HashEntry("city", "london")]);
        await Db.ListRightPushAsync("queue", ["first", "second", "third"]);
        await Db.SetAddAsync("tags", ["red", "green", "blue"]);
        await Db.SortedSetAddAsync("scores", [new SortedSetEntry("ada", 10), new SortedSetEntry("linus", 20)]);
        await Db.StreamAddAsync("events", [new NameValueEntry("kind", "signup")]);
        await Db.StringSetAsync("temporary", "gone soon", TimeSpan.FromMinutes(10));
    }

    public async ValueTask DisposeAsync()
    {
        await Multiplexer.CloseAsync();
        await _container.DisposeAsync();
    }
}

public class ValueTests(ValueFixture fixture) : IClassFixture<ValueFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<ValueDto?> Read(string key) =>
        RedisValues.ReadAsync(fixture.Db, key, 0, RedisValues.PageSize, Ct);

    [Fact]
    public async Task A_string_comes_back_as_a_string_with_its_length()
    {
        var value = await Read("greeting");

        Assert.NotNull(value);
        Assert.Equal("string", value!.Type);
        Assert.Equal("hello", value.Value.GetString());
        Assert.Equal(5, value.Length);
        Assert.Null(value.TtlSeconds);
    }

    [Fact]
    public async Task A_hash_comes_back_as_an_object()
    {
        var value = await Read("profile:1");

        Assert.Equal("hash", value!.Type);
        Assert.Equal("ada", value.Value.GetProperty("name").GetString());
        Assert.Equal("london", value.Value.GetProperty("city").GetString());
        Assert.Equal(2, value.Length);
    }

    [Fact]
    public async Task A_list_keeps_its_order()
    {
        var value = await Read("queue");

        Assert.Equal("list", value!.Type);
        Assert.Equal(["first", "second", "third"],
            value.Value.EnumerateArray().Select(entry => entry.GetString()).ToArray());
    }

    [Fact]
    public async Task A_set_comes_back_as_its_members()
    {
        var value = await Read("tags");

        Assert.Equal("set", value!.Type);
        var members = value.Value.EnumerateArray().Select(entry => entry.GetString()).ToList();
        Assert.Equal(3, members.Count);
        Assert.Contains("red", members);
    }

    [Fact]
    public async Task A_sorted_set_carries_the_scores()
    {
        var value = await Read("scores");

        Assert.Equal("zset", value!.Type);
        var first = value.Value.EnumerateArray().First();
        Assert.Equal("ada", first.GetProperty("member").GetString());
        Assert.Equal(10, first.GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task A_stream_carries_its_entries_with_their_ids()
    {
        var value = await Read("events");

        Assert.Equal("stream", value!.Type);
        var entry = value.Value.EnumerateArray().First();
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("id").GetString()));
        Assert.Equal("signup", entry.GetProperty("values").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_ttl_is_reported_in_seconds()
    {
        var value = await Read("temporary");

        Assert.NotNull(value!.TtlSeconds);
        Assert.InRange(value.TtlSeconds!.Value, 1, 600);
    }

    [Fact]
    public async Task The_encoding_comes_along_because_that_is_what_memory_questions_start_with()
    {
        var value = await Read("profile:1");
        Assert.False(string.IsNullOrWhiteSpace(value!.Encoding));
    }

    [Fact]
    public async Task A_key_that_does_not_exist_is_null_rather_than_an_error()
    {
        Assert.Null(await Read("nothing-here"));
    }

    // --- planning a write ------------------------------------------------------------------------

    private static ValueEditRequest Edit(string key, string operation, object payload) =>
        new(0, key, operation, JsonSerializer.SerializeToElement(payload));

    [Fact]
    public void An_edit_is_planned_as_the_command_it_will_run()
    {
        var commands = RedisValues.Plan(Edit("profile:1", "hset", new { field = "city", value = "berlin" }));

        Assert.Equal(["HSET profile:1 city berlin"], commands);
    }

    [Fact]
    public void A_value_with_spaces_is_quoted_the_way_redis_cli_would()
    {
        var commands = RedisValues.Plan(Edit("greeting", "set", new { value = "hello there" }));

        Assert.Equal(["SET greeting \"hello there\""], commands);
    }

    [Fact]
    public void An_unknown_operation_is_refused_rather_than_guessed()
    {
        Assert.Throws<ArgumentException>(() =>
            RedisValues.Plan(Edit("greeting", "frobnicate", new { value = "x" })));
    }

    [Fact]
    public void Removing_data_is_marked_destructive()
    {
        Assert.True(RedisValues.IsDestructive("del"));
        Assert.True(RedisValues.IsDestructive("hdel"));
        Assert.False(RedisValues.IsDestructive("expire"));
    }

    // --- applying it ----------------------------------------------------------------------------

    [Fact]
    public async Task Applying_a_planned_command_changes_exactly_that()
    {
        var key = $"apply:{Guid.NewGuid():N}";
        var commands = RedisValues.Plan(Edit(key, "set", new { value = "written" }));

        var executed = await RedisValues.ApplyAsync(fixture.Db, 0, commands, Ct);

        Assert.Equal(1, executed);
        Assert.Equal("written", await fixture.Db.StringGetAsync(key));
    }

    [Fact]
    public async Task A_quoted_value_survives_the_round_trip_through_the_preview_text()
    {
        var key = $"quoted:{Guid.NewGuid():N}";
        // The preview text is what Apply executes, so quoting and unquoting have to agree.
        var commands = RedisValues.Plan(Edit(key, "set", new { value = "two words" }));

        await RedisValues.ApplyAsync(fixture.Db, 0, commands, Ct);

        Assert.Equal("two words", await fixture.Db.StringGetAsync(key));
    }

    [Fact]
    public async Task Expiring_and_persisting_a_key_both_work()
    {
        var key = $"ttl:{Guid.NewGuid():N}";
        await fixture.Db.StringSetAsync(key, "value");

        await RedisValues.ApplyAsync(fixture.Db, 0,
            RedisValues.Plan(Edit(key, "expire", new { seconds = 120 })), Ct);
        Assert.NotNull(await fixture.Db.KeyTimeToLiveAsync(key));

        await RedisValues.ApplyAsync(fixture.Db, 0,
            RedisValues.Plan(Edit(key, "persist", new { })), Ct);
        Assert.Null(await fixture.Db.KeyTimeToLiveAsync(key));
    }

    [Fact]
    public void The_hash_of_a_plan_is_stable_and_specific()
    {
        var one = RedisValues.HashOf(["SET a b"]);
        var same = RedisValues.HashOf(["SET a b"]);
        var other = RedisValues.HashOf(["SET a c"]);

        Assert.Equal(one, same);
        Assert.NotEqual(one, other);
    }
}
