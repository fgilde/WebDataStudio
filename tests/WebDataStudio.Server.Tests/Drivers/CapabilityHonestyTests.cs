using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Drivers;

/// A capability declared false must fail loudly and predictably, not obscurely. This runs without
/// a live database: it only needs the driver instances.
public class CapabilityHonestyTests
{
    public static TheoryData<string> Engines()
    {
        var data = new TheoryData<string>();
        foreach (var driver in new DriverRegistry().All()) data.Add(driver.Info.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Explain_throws_NotSupportedException_when_the_capability_is_false(string engine)
    {
        var driver = new DriverRegistry().Get(engine);
        if (driver.Caps.EstimatedPlan) return;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            driver.ExplainAsync(null!, "SELECT 1", PlanMode.Estimated, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Driver_metadata_is_complete(string engine)
    {
        var driver = new DriverRegistry().Get(engine);
        Assert.False(string.IsNullOrWhiteSpace(driver.Info.Label));
        Assert.False(string.IsNullOrWhiteSpace(driver.Info.ConnectionStringTemplate));
        Assert.NotNull(driver.Dialect);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Sql_engines_expose_a_working_dialect(string engine)
    {
        var driver = new DriverRegistry().Get(engine);
        if (!driver.Caps.Sql) return;

        Assert.NotEqual("x", driver.Dialect.QuoteIdentifier("x"));
        Assert.NotEmpty(driver.Dialect.Paginate("SELECT 1", 0, 10));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Every_engine_the_connection_form_offers_has_a_capability_set(string engine)
    {
        var driver = new DriverRegistry().Get(engine);
        Assert.NotNull(driver.Caps);
        // A driver that claims actual plans must also claim estimated ones: the UI offers the
        // estimated toggle as the cheaper default and would otherwise show a dead option.
        if (driver.Caps.ActualPlan) Assert.True(driver.Caps.EstimatedPlan);
    }
}
