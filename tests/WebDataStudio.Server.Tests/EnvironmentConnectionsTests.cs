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
    public void A_name_with_two_underscores_keeps_them()
    {
        // ASP.NET's environment provider maps a double underscore to a colon, so a connection an
        // orchestrator called "ABP - SPARK" (WDS_CONN_ABP___SPARK) used to show up as
        // "ABP:_SPARK" in the explorer.
        var result = EnvironmentConnections.Parse(new Dictionary<string, string?>
        {
            ["WDS_CONN_ABP:_SPARK"] = "Data Source=/tmp/shop.db",
            ["WDS_CONN_ABP:_SPARK_ENGINE"] = "sqlite",
        });

        var spec = Assert.Single(result);
        Assert.Equal("ABP___SPARK", spec.Name);
        Assert.Equal("sqlite", spec.Engine);
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

public class ProviderConnectionStringTests
{
    private static ConnectionSpec Parse(params (string Key, string Value)[] env) =>
        Assert.Single(EnvironmentConnections.Parse(
            env.ToDictionary(e => e.Key, e => (string?)e.Value)));

    [Fact]
    public void An_engine_variable_makes_a_provider_string_usable()
    {
        // The shape an orchestrator hands over: the resource's own connection string.
        var spec = Parse(
            ("WDS_CONN_SHOP", "Host=db;Port=5432;Username=app;Password=pw;Database=shop"),
            ("WDS_CONN_SHOP_ENGINE", "postgresql"));

        Assert.Equal("SHOP", spec.Name);
        Assert.Equal("postgresql", spec.Engine);
        Assert.Equal("Host=db;Port=5432;Username=app;Password=pw;Database=shop", spec.ConnectionString);
    }

    [Fact]
    public void The_engine_variable_is_not_a_connection_of_its_own()
    {
        var specs = EnvironmentConnections.Parse(new Dictionary<string, string?>
        {
            ["WDS_CONN_SHOP"] = "Host=db;Username=app",
            ["WDS_CONN_SHOP_ENGINE"] = "postgresql",
            ["WDS_CONN_SHOP_READONLY"] = "true",
            ["WDS_CONN_SHOP_GROUP"] = "Production",
            ["WDS_CONN_SHOP_COLOR"] = "#e03131",
        });

        var spec = Assert.Single(specs);
        Assert.True(spec.ReadOnly);
        Assert.Equal("Production", spec.Group);
        Assert.Equal("#e03131", spec.Color);
    }

    [Theory]
    [InlineData("Host=db;Port=5432;Username=app;Password=pw;Database=shop", "postgresql")]
    [InlineData("Server=db;Port=3306;Uid=root;Pwd=pw;Database=shop", "mysql")]
    [InlineData("Server=db,1433;Database=shop;User Id=sa;Password=pw;TrustServerCertificate=True", "sqlserver")]
    [InlineData("Data Source=/data/local.db", "sqlite")]
    [InlineData("Data Source=/data/analytics.duckdb", "duckdb")]
    [InlineData("mongodb://db:27017/shop", "mongodb")]
    public void The_engine_is_guessed_when_nothing_declares_it(string connectionString, string engine) =>
        Assert.Equal(engine, Parse(("WDS_CONN_X", connectionString)).Engine);

    [Fact]
    public void A_value_that_is_neither_a_url_nor_a_recognised_string_is_skipped() =>
        Assert.Empty(EnvironmentConnections.Parse(new Dictionary<string, string?>
        {
            ["WDS_CONN_MYSTERY"] = "something=else",
        }));

    [Fact]
    public void A_declared_engine_wins_over_the_scheme_of_a_url()
    {
        // A MariaDB URL is served by the MySQL driver either way, but the caller may know better.
        var spec = Parse(
            ("WDS_CONN_X", "mysql://app:pw@db:3306/shop"),
            ("WDS_CONN_X_ENGINE", "mysql"));

        Assert.Equal("mysql", spec.Engine);
        Assert.Contains("Server=db", spec.ConnectionString);
    }

    [Fact]
    public void The_read_only_flag_defaults_to_false() =>
        Assert.False(Parse(("WDS_CONN_X", "postgres://app:pw@db:5432/shop")).ReadOnly);
}
