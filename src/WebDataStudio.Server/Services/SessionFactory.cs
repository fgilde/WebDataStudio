using System.Data.Common;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

public sealed class UnknownConnectionException(string id)
    : Exception($"no connection with id '{id}'");

/// Holds the tunnel open for exactly as long as the session that needs it.
internal sealed class TunnelledSession(
    IDbSession inner, TunnelManager tunnels, TunnelSpec spec, string host, int port) : IDbSession
{
    public ConnectionSpec Spec => inner.Spec;
    public DbConnection Connection => inner.Connection;

    public async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        tunnels.Release(spec, host, port);
    }
}

public sealed class SessionFactory(
    ConnectionRegistry registry, DriverRegistry drivers, TunnelManager tunnels, SessionPool pool)
{
    public async Task<(IDbDriver Driver, IDbSession Session)> OpenAsync(string connectionId, CancellationToken ct)
    {
        var spec = registry.Find(connectionId) ?? throw new UnknownConnectionException(connectionId);
        var driver = drivers.Get(spec.Engine);

        return (driver, await pool.RentAsync(connectionId, token => OpenRawAsync(spec, driver, token), ct));
    }

    private async Task<IDbSession> OpenRawAsync(ConnectionSpec spec, IDbDriver driver, CancellationToken ct)
    {
        if (spec.Tunnel is not { } tunnel) return await driver.OpenAsync(spec, ct);

        var (host, port) = ConnectionEndpoint.Of(spec.Engine, spec.ConnectionString);
        var local = tunnels.Ensure(tunnel, host, port);

        // The session carries the rewritten connection string, so anything that shells out with
        // it — pg_dump, mysqldump — travels through the same tunnel instead of failing on a host
        // only the jump host can resolve.
        var tunnelled = spec with
        {
            ConnectionString = ConnectionEndpoint.Rewrite(spec.Engine, spec.ConnectionString,
                local.Host, local.Port),
        };

        try
        {
            var session = await driver.OpenAsync(tunnelled, ct);
            return new TunnelledSession(session, tunnels, tunnel, host, port);
        }
        catch (Exception)
        {
            tunnels.Release(tunnel, host, port);
            throw;
        }
    }
}
