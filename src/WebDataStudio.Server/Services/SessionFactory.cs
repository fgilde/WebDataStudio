using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

public sealed class UnknownConnectionException(string id)
    : Exception($"no connection with id '{id}'");

public sealed class SessionFactory(ConnectionRegistry registry, DriverRegistry drivers)
{
    public async Task<(IDbDriver Driver, IDbSession Session)> OpenAsync(string connectionId, CancellationToken ct)
    {
        var spec = registry.Find(connectionId) ?? throw new UnknownConnectionException(connectionId);
        var driver = drivers.Get(spec.Engine);
        return (driver, await driver.OpenAsync(spec, ct));
    }
}
