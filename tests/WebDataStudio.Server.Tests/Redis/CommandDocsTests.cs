using StackExchange.Redis;
using Testcontainers.Redis;
using WebDataStudio.Server.Drivers.Redis;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Redis;

/// The console's help and the cluster view. Both read the server rather than a list baked into the
/// studio: a server with modules has commands no such list knows, and a standalone server has to
/// answer the cluster question too instead of failing.
public class CommandDocsTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private IConnectionMultiplexer _multiplexer = null!;
    private RedisSession _session = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(
            _container.GetConnectionString() + ",allowAdmin=true");

        _session = new RedisSession(
            new ConnectionSpec("t", "test", "redis", _container.GetConnectionString(),
                false, null, null, ConnectionSource.Stored),
            _multiplexer, 0);
    }

    public async ValueTask DisposeAsync()
    {
        await _multiplexer.CloseAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task The_help_carries_arity_and_a_summary()
    {
        var commands = await RedisCommandDocs.ListAsync(_session);

        var hset = commands.FirstOrDefault(c => c.Name == "HSET");

        Assert.NotNull(hset);
        // HSET takes a key and at least one field/value pair, so its arity is variable.
        Assert.True(hset!.Arity < 0);
        Assert.Contains("hash", hset.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hash", hset.Group);
    }

    [Fact]
    public async Task Enough_commands_come_back_to_complete_from()
    {
        var commands = await RedisCommandDocs.ListAsync(_session);

        Assert.True(commands.Count > 100, $"only {commands.Count} commands came back");
        Assert.Contains(commands, c => c.Name == "GET");
        Assert.Contains(commands, c => c.Name == "SCAN");
        // Container commands are reported under their container name.
        Assert.Contains(commands, c => c.Name.StartsWith("CLIENT", StringComparison.Ordinal));
    }

    /// A standalone server is the common case. Answering "there is no cluster, and here is the one
    /// node" is more useful than an error the caller has to interpret.
    [Fact]
    public async Task A_standalone_server_reports_itself_as_one_node()
    {
        var cluster = await RedisCommandDocs.DescribeAsync(_session);

        Assert.False(cluster.Enabled);
        var node = Assert.Single(cluster.Nodes);
        Assert.Equal("master", node.Role);
        Assert.True(node.Connected);
        Assert.Equal(1, cluster.KnownNodes);
    }
}
