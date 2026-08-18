using WebDataStudio.Server.Drivers;

namespace WebDataStudio.Server.Tests;

public class DriverRegistryTests
{
    [Theory]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void Resolves_every_tier_one_engine(string engine) =>
        Assert.NotNull(new DriverRegistry().Get(engine));

    [Fact]
    public void Unknown_engine_throws() =>
        Assert.Throws<NotSupportedException>(() => new DriverRegistry().Get("notadb"));

    [Fact]
    public void Driver_ids_match_their_registry_key()
    {
        var registry = new DriverRegistry();
        foreach (var driver in registry.All())
            Assert.Same(driver, registry.Get(driver.Info.Id));
    }
}
