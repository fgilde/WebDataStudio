using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class EnvironmentConnectionsTests
{
    [Fact]
    public void Returns_nothing_without_configuration() =>
        Assert.Empty(EnvironmentConnections.Parse(new Dictionary<string, string?>()));

    [Fact]
    public void Parses_a_single_url_variable()
    {
        var result = EnvironmentConnections.Parse(new Dictionary<string, string?>
        {
            ["WDS_CONN_PROD"] = "postgres://app:pw@db:5432/shop",
        });

        var spec = Assert.Single(result);
        Assert.Equal("PROD", spec.Name);
        Assert.Equal("postgresql", spec.Engine);
        Assert.Equal(ConnectionSource.Environment, spec.Source);
        Assert.Contains("Host=db", spec.ConnectionString);
    }

    [Fact]
    public void Parses_the_json_array_variable()
    {
        var result = EnvironmentConnections.Parse(new Dictionary<string, string?>
        {
            ["WDS_CONNECTIONS"] = """
            [{"name":"prod-pg","engine":"postgresql","connectionString":"Host=db;Database=shop",
              "readOnly":true,"color":"red","group":"Production"}]
            """,
        });

        var spec = Assert.Single(result);
        Assert.Equal("prod-pg", spec.Name);
        Assert.True(spec.ReadOnly);
        Assert.Equal("red", spec.Color);
        Assert.Equal("Production", spec.Group);
    }

    [Fact]
    public void Skips_a_malformed_entry_and_keeps_the_rest()
    {
        var result = EnvironmentConnections.Parse(new Dictionary<string, string?>
        {
            ["WDS_CONN_BROKEN"] = "not a url",
            ["WDS_CONN_GOOD"] = "postgres://app:pw@db/shop",
        });

        Assert.Equal("GOOD", Assert.Single(result).Name);
    }

    [Fact]
    public void Ids_are_stable_across_restarts()
    {
        var env = new Dictionary<string, string?> { ["WDS_CONN_PROD"] = "postgres://app:pw@db/shop" };
        Assert.Equal(
            EnvironmentConnections.Parse(env).Single().Id,
            EnvironmentConnections.Parse(env).Single().Id);
    }
}
