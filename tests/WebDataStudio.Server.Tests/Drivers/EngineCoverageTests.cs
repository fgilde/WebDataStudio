using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests.Drivers;

/// One engine list, three places. This test is what keeps them from drifting.
public class EngineCoverageTests
{
    private static readonly string[] Expected =
    [
        "clickhouse", "duckdb", "mongodb", "mysql", "oracle", "postgresql", "redis", "sqlite", "sqlserver",
    ];

    [Fact]
    public void The_registry_carries_every_expected_engine() =>
        Assert.Equal(Expected, new DriverRegistry().All().Select(d => d.Info.Id).Order().ToArray());

    [Fact]
    public void The_connection_form_offers_exactly_those_engines() =>
        Assert.Equal(Expected, ConnectionRegistry.KnownEngines.Order().ToArray());

    [Fact]
    public void Every_engine_the_form_offers_has_a_driver()
    {
        var registry = new DriverRegistry();
        foreach (var engine in ConnectionRegistry.KnownEngines) Assert.NotNull(registry.Get(engine));
    }
}
