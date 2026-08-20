using StackExchange.Redis;
using Testcontainers.Redis;
using WebDataStudio.Server.Drivers.Redis;

namespace WebDataStudio.Server.Tests.Redis;

/// The analysis panel and the administrative reads behind it: where the memory went, what a stream's
/// consumers are doing, what was slow, who is connected.
public class AnalysisAndOperationsTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private IConnectionMultiplexer _multiplexer = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IDatabase Db => _multiplexer.GetDatabase();
    private IServer Server => _multiplexer.GetServer(_multiplexer.GetEndPoints()[0]);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(
            _container.GetConnectionString() + ",allowAdmin=true");

        for (var index = 0; index < 200; index++) await Db.StringSetAsync($"user:{index}", "small");
        for (var index = 0; index < 20; index++) await Db.HashSetAsync($"cart:{index}",
            [new HashEntry("items", "3")]);

        await Db.StringSetAsync("blob:big", new string('x', 120_000));
        await Db.StringSetAsync("session:short", "x", TimeSpan.FromSeconds(90));
        await Db.StringSetAsync("flat-key", "no prefix here");
    }

    public async ValueTask DisposeAsync()
    {
        await _multiplexer.CloseAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task The_analysis_groups_memory_by_prefix()
    {
        var analysis = await RedisAnalysis.RunAsync(_multiplexer, 0, 5_000, Ct);

        Assert.True(analysis.Complete);
        Assert.Contains(analysis.Prefixes, prefix => prefix.Prefix == "user" && prefix.Keys == 200);
        Assert.Contains(analysis.Prefixes, prefix => prefix.Prefix == "cart" && prefix.Keys == 20);
        // A key without a separator still has to appear somewhere.
        Assert.Contains(analysis.Prefixes, prefix => prefix.Prefix == "(no prefix)");
    }

    [Fact]
    public async Task It_counts_the_types()
    {
        var analysis = await RedisAnalysis.RunAsync(_multiplexer, 0, 5_000, Ct);

        Assert.Contains(analysis.Types, type => type.Type == "hash" && type.Keys == 20);
        Assert.Contains(analysis.Types, type => type.Type == "string");
    }

    [Fact]
    public async Task The_biggest_key_is_first_in_the_largest_list()
    {
        var analysis = await RedisAnalysis.RunAsync(_multiplexer, 0, 5_000, Ct);

        Assert.Equal("blob:big", analysis.Largest[0].Key);
        Assert.True(analysis.Largest[0].SizeBytes > 100_000);
    }

    [Fact]
    public async Task What_expires_soonest_comes_first()
    {
        var analysis = await RedisAnalysis.RunAsync(_multiplexer, 0, 5_000, Ct);

        Assert.Equal("session:short", analysis.ExpiringSoon[0].Key);
    }

    [Fact]
    public async Task It_reports_the_server_totals_next_to_the_sample()
    {
        var analysis = await RedisAnalysis.RunAsync(_multiplexer, 0, 5_000, Ct);

        Assert.NotNull(analysis.TotalMemoryBytes);
        Assert.True(analysis.TotalMemoryBytes > 0);
        Assert.Equal(223, analysis.TotalKeys);
    }

    // Sampling is the point: a keyspace larger than the sample must not be walked to the end.
    [Fact]
    public async Task A_sample_smaller_than_the_keyspace_says_so()
    {
        var analysis = await RedisAnalysis.RunAsync(_multiplexer, 0, 50, Ct);

        Assert.False(analysis.Complete);
        Assert.True(analysis.SampledKeys < analysis.TotalKeys);
    }

    [Fact]
    public async Task A_stream_reports_its_groups_and_what_is_pending()
    {
        await Db.StreamAddAsync("events", [new NameValueEntry("kind", "signup")]);
        await Db.StreamCreateConsumerGroupAsync("events", "workers", "0-0");
        // Read without acknowledging: that is exactly what "pending" means.
        await Db.StreamReadGroupAsync("events", "workers", "worker-1", ">");

        var info = await RedisOperations.StreamAsync(Db, "events", Ct);

        Assert.Equal(1, info.Length);
        var group = Assert.Single(info.Groups);
        Assert.Equal("workers", group.Name);
        Assert.Equal(1, group.Pending);
        Assert.Equal("worker-1", Assert.Single(info.Pending).Consumer);
    }

    [Fact]
    public async Task The_slow_log_returns_what_the_server_recorded()
    {
        // Log everything, run one command, and it has to show up.
        await Server.ConfigSetAsync("slowlog-log-slower-than", "0");
        await Db.StringGetAsync("user:1");

        var entries = await RedisOperations.SlowLogAsync(Server, 20, Ct);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, entry => entry.Command.Contains("GET", StringComparison.OrdinalIgnoreCase));
        await Server.ConfigSetAsync("slowlog-log-slower-than", "10000");
    }

    [Fact]
    public async Task The_client_list_includes_this_connection()
    {
        var clients = await RedisOperations.ClientsAsync(Server, Ct);

        Assert.NotEmpty(clients);
        Assert.All(clients, client => Assert.False(string.IsNullOrWhiteSpace(client.Id)));
    }
}
