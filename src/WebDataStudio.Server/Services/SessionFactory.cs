using System.Data.Common;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

public sealed class UnknownConnectionException(string id)
    : Exception($"no connection with id '{id}'");

/// This connection is one a person signs in to, and nobody has. The endpoints turn it into an
/// answer the UI can act on — "sign in" — rather than into an authentication failure.
public sealed class EntraSignInRequiredException(string id)
    : Exception($"connection '{id}' needs an interactive Entra sign-in");

/// Holds the tunnel open for exactly as long as the session that needs it.
internal sealed class TunnelledSession(
    IDbSession inner, TunnelManager tunnels, TunnelSpec spec, string host, int port) : IDbSessionWrapper
{
    public IDbSession Inner => inner;
    public ConnectionSpec Spec => inner.Spec;
    public DbConnection Connection => inner.Connection;

    public async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        tunnels.Release(spec, host, port);
    }
}

public sealed class SessionFactory(
    ConnectionRegistry registry, DriverRegistry drivers, TunnelManager tunnels, SessionPool pool,
    EntraSignIn entra)
{
    public async Task<(IDbDriver Driver, IDbSession Session)> OpenAsync(string connectionId, CancellationToken ct)
    {
        var spec = registry.Find(connectionId) ?? throw new UnknownConnectionException(connectionId);
        var driver = drivers.Get(spec.Engine);

        // A connection that says a person signs in cannot be opened by the machine. Where somebody
        // has signed in, their token comes along; where nobody has, the open fails with a message
        // that says to sign in rather than with an authentication error nobody can act on.
        if (EntraConnectionString.WantsAPerson(spec.ConnectionString))
            spec = entra.TokenFor(connectionId) is { Length: > 0 } token
                ? spec with { AccessToken = token }
                : throw new EntraSignInRequiredException(connectionId);

        return (driver, await pool.RentAsync(connectionId, token => OpenRawAsync(spec, driver, token), ct));
    }

    /// A session of its own, outside the pool, for the two things a pooled one cannot do: stay open
    /// for as long as somebody watches a stream, and end up in a state the pool cannot hand on.
    ///
    /// PostgreSQL's LISTEN is both. It parks its connection in `WaitAsync` — where Npgsql leaves the
    /// connector in `Waiting` — and holds it for as long as the browser keeps the stream. Returned to
    /// the pool, that connection makes the next renter, and the shutdown, throw
    /// `NpgsqlOperationInProgressException`; held for an hour, it is one of the four sessions a
    /// studio has. So a listener owns its connection and closes it with the request.
    public async Task<(IDbDriver Driver, IDbSession Session)> OpenExclusiveAsync(
        string connectionId, CancellationToken ct)
    {
        var spec = registry.Find(connectionId) ?? throw new UnknownConnectionException(connectionId);
        var driver = drivers.Get(spec.Engine);

        if (EntraConnectionString.WantsAPerson(spec.ConnectionString))
            spec = entra.TokenFor(connectionId) is { Length: > 0 } token
                ? spec with { AccessToken = token }
                : throw new EntraSignInRequiredException(connectionId);

        return (driver, await OpenRawAsync(spec, driver, ct));
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
