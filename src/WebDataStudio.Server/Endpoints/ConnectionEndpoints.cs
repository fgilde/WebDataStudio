using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class ConnectionEndpoints
{
    public record ConnectionRequest(string Name, string Engine, string ConnectionString,
        bool ReadOnly, string? Color, string? Group, TunnelSpec? Tunnel = null);

    public static void MapConnectionEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/connections");

        api.MapGet("/", (ConnectionRegistry registry) =>
            Results.Ok(registry.All().Select(ConnectionRegistry.ToDto)));

        api.MapPost("/", (ConnectionRequest body, ConnectionStore store) =>
        {
            if (Validate(body) is { } error) return error;
            try
            {
                var added = store.Add(new ConnectionSpec("", body.Name.Trim(), body.Engine,
                    body.ConnectionString, body.ReadOnly, body.Color, body.Group, ConnectionSource.Stored,
                    body.Tunnel));
                return Results.Ok(ConnectionRegistry.ToDto(added));
            }
            catch (InvalidOperationException e)
            {
                return Results.Conflict(new { message = e.Message });
            }
        });

        api.MapPut("/{id}", (string id, ConnectionRequest body, ConnectionRegistry registry, ConnectionStore store) =>
        {
            if (registry.Find(id) is not { } existing) return Results.NotFound();
            if (existing.Source == ConnectionSource.Environment) return EnvironmentIsReadOnly();
            if (Validate(body) is { } error) return error;

            store.Update(existing with
            {
                Name = body.Name.Trim(),
                Engine = body.Engine,
                ConnectionString = body.ConnectionString,
                ReadOnly = body.ReadOnly,
                Color = body.Color,
                Group = body.Group,
                // A form that posts no tunnel keeps the stored one: the private key is never sent
                // back to the browser, so an edit cannot round-trip it.
                Tunnel = body.Tunnel ?? existing.Tunnel,
            });
            return Results.Ok(ConnectionRegistry.ToDto(registry.Find(id)!));
        });

        api.MapDelete("/{id}", (string id, ConnectionRegistry registry, ConnectionStore store) =>
        {
            if (registry.Find(id) is not { } existing) return Results.NotFound();
            if (existing.Source == ConnectionSource.Environment) return EnvironmentIsReadOnly();

            store.Delete(id);
            return Results.NoContent();
        });

        api.MapPost("/test", async (ConnectionRequest body, DriverRegistry drivers,
            TunnelManager tunnels, CancellationToken ct) =>
        {
            if (Validate(body) is { } error) return error;

            TunnelSpec? opened = null;
            (string Host, int Port) target = default;

            try
            {
                var driver = drivers.Get(body.Engine);
                var connectionString = body.ConnectionString;

                if (body.Tunnel is { } tunnel)
                {
                    target = ConnectionEndpoint.Of(body.Engine, connectionString);
                    var local = tunnels.Ensure(tunnel, target.Host, target.Port);
                    opened = tunnel;
                    connectionString = ConnectionEndpoint.Rewrite(body.Engine, connectionString,
                        local.Host, local.Port);
                }

                var spec = new ConnectionSpec("probe", body.Name, body.Engine, connectionString,
                    true, null, null, ConnectionSource.Stored);
                await using var session = await driver.OpenAsync(spec, ct);
                return Results.Ok(new { ok = true, message = $"connected to {driver.Info.Label}" });
            }
            catch (Exception e)
            {
                // A failed probe is information, not a server fault: 200 with ok=false keeps the form simple.
                return Results.Ok(new { ok = false, message = e.Message });
            }
            finally
            {
                if (opened is not null) tunnels.Release(opened, target.Host, target.Port);
            }
        });
    }

    private static IResult EnvironmentIsReadOnly() =>
        Results.Json(new { message = "connections defined in the environment are read-only" },
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult? Validate(ConnectionRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { message = "name required" });
        if (!ConnectionRegistry.KnownEngines.Contains(body.Engine))
            return Results.BadRequest(new { message = $"unknown engine '{body.Engine}'" });
        if (string.IsNullOrWhiteSpace(body.ConnectionString))
            return Results.BadRequest(new { message = "connection string required" });
        return null;
    }
}
