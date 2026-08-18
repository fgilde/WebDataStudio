using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class ConnectionUrlTests
{
    [Theory]
    [InlineData("postgres", "postgresql")]
    [InlineData("postgresql", "postgresql")]
    [InlineData("mysql", "mysql")]
    [InlineData("mariadb", "mysql")]
    [InlineData("sqlserver", "sqlserver")]
    [InlineData("mssql", "sqlserver")]
    [InlineData("sqlite", "sqlite")]
    [InlineData("oracle", "oracle")]
    [InlineData("duckdb", "duckdb")]
    [InlineData("clickhouse", "clickhouse")]
    [InlineData("mongodb", "mongodb")]
    [InlineData("redis", "redis")]
    public void Maps_scheme_to_engine(string scheme, string engine) =>
        Assert.Equal(engine, ConnectionUrl.EngineFromScheme(scheme));

    [Fact]
    public void Unknown_scheme_returns_null() =>
        Assert.Null(ConnectionUrl.EngineFromScheme("ftp"));

    [Fact]
    public void Builds_postgres_connection_string()
    {
        var result = ConnectionUrl.ToAdoConnectionString("postgresql", new Uri("postgres://app:pw@db:5432/shop"));
        Assert.Contains("Host=db", result);
        Assert.Contains("Port=5432", result);
        Assert.Contains("Database=shop", result);
        Assert.Contains("Username=app", result);
        Assert.Contains("Password=pw", result);
    }

    [Fact]
    public void Uses_the_engine_default_port_when_the_url_omits_it()
    {
        var result = ConnectionUrl.ToAdoConnectionString("mysql", new Uri("mysql://root:pw@db/shop"));
        Assert.Contains("Port=3306", result);
    }

    [Fact]
    public void Decodes_percent_escapes_in_the_password()
    {
        var result = ConnectionUrl.ToAdoConnectionString("postgresql", new Uri("postgres://app:p%40ss@db/shop"));
        Assert.Contains("Password=p@ss", result);
    }

    [Fact]
    public void Builds_sqlserver_connection_string()
    {
        var result = ConnectionUrl.ToAdoConnectionString("sqlserver", new Uri("sqlserver://sa:pw@db:1433/shop"));
        Assert.Contains("Server=db,1433", result);
        Assert.Contains("Database=shop", result);
        Assert.Contains("User Id=sa", result);
        Assert.Contains("TrustServerCertificate=True", result);
    }

    [Fact]
    public void Builds_sqlite_connection_string_from_a_path()
    {
        var result = ConnectionUrl.ToAdoConnectionString("sqlite", new Uri("sqlite:///data/shop.db"));
        Assert.Equal("Data Source=/data/shop.db", result);
    }

    [Fact]
    public void Passes_mongodb_and_redis_urls_through_unchanged()
    {
        Assert.Equal("mongodb://db:27017/shop",
            ConnectionUrl.ToAdoConnectionString("mongodb", new Uri("mongodb://db:27017/shop")));
        Assert.Equal("redis://cache:6379",
            ConnectionUrl.ToAdoConnectionString("redis", new Uri("redis://cache:6379")));
    }
}
