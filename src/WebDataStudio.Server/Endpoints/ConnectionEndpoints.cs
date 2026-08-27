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

        // --- presets and the interactive Entra sign-in -----------------------
        // The connection strings nobody remembers, and the flow for the ones a person signs in to.
        app.MapGet("/api/connection-presets", (string? engine) =>
            Results.Ok(ConnectionPresets.For(engine)));

        // Starts a device-code sign-in and returns at once: the code arrives on the next poll,
        // because the person needs a moment to get to a browser.
        api.MapPost("/{id}/entra/signin", (string id, string? tenant, EntraSignIn entra,
            CancellationToken ct) => Results.Ok(entra.Start(id, tenant, ct)));

        api.MapGet("/{id}/entra", (string id, EntraSignIn entra) => Results.Ok(entra.Status(id)));

        api.MapDelete("/{id}/entra", (string id, EntraSignIn entra) =>
        {
            entra.SignOut(id);
            return Results.NoContent();
        });

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

        api.MapPut("/{id}", async (string id, ConnectionRequest body, ConnectionRegistry registry,
            ConnectionStore store, SessionPool pool) =>
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

            // Pooled sessions still point at the old target, so they go.
            await pool.EvictAsync(id);
            return Results.Ok(ConnectionRegistry.ToDto(registry.Find(id)!));
        });

        api.MapDelete("/{id}", async (string id, ConnectionRegistry registry, ConnectionStore store,
            SessionPool pool) =>
        {
            if (registry.Find(id) is not { } existing) return Results.NotFound();
            if (existing.Source == ConnectionSource.Environment) return EnvironmentIsReadOnly();

            store.Delete(id);
            await pool.EvictAsync(id);
            return Results.NoContent();
        });

        // Everything about one connection, for the properties dialog. The connection string is
        // masked; the caller has to ask for the password on purpose (see below).
        api.MapGet("/{id}/properties", async (string id, ConnectionRegistry registry,
            DriverRegistry drivers, SessionFactory factory, CancellationToken ct) =>
        {
            if (registry.Find(id) is not { } spec) return Results.NotFound();

            var driver = drivers.Get(spec.Engine);
            var properties = new List<PropertyEntry>
            {
                new("Connection", "Name", spec.Name),
                new("Connection", "Engine", driver.Info.Label),
                new("Connection", "Defined in", spec.Source == ConnectionSource.Environment
                    ? "the environment (read-only in the UI)"
                    : "this studio"),
                new("Connection", "Access", spec.ReadOnly ? "read-only" : "read and write"),
                new("Connection", "Group", spec.Group),
                new("Connection", "Colour", spec.Color),
                new("Connection", "SSH tunnel", spec.Tunnel is { } tunnel
                    ? $"{tunnel.User}@{tunnel.Host}:{tunnel.Port}"
                    : null),
            };

            string? failure = null;
            try
            {
                var (_, session) = await factory.OpenAsync(id, ct);
                await using (session)
                    properties.AddRange(await ConnectionProperties.ReadAsync(driver, session, ct));
            }
            catch (Exception e)
            {
                // The definition is worth showing even when the server is unreachable — that is
                // often exactly what the dialog is opened to find out.
                failure = e.Message;
            }

            return Results.Ok(new
            {
                connectionString = ConnectionSecret.Hide(spec.ConnectionString),
                hasPassword = ConnectionSecret.HasPassword(spec.ConnectionString),
                reachable = failure is null,
                error = failure,
                capabilities = driver.Caps,
                properties = properties.Where(p => p.Value is not null),
            });
        });

        // The connection string with its password, asked for explicitly. Separate from the
        // properties call so a password is never part of a routine page load.
        api.MapPost("/{id}/reveal", (string id, ConnectionRegistry registry) =>
            registry.Find(id) is { } spec
                ? Results.Ok(new { connectionString = spec.ConnectionString })
                : Results.NotFound());

        api.MapGet("/export", (ConnectionRegistry registry) =>
            Results.Ok(registry.All().Select(ToPortable)));

        api.MapPost("/import", (List<PortableConnection> body, ConnectionStore store) =>
        {
            var imported = new List<string>();
            var skipped = new List<object>();

            foreach (var entry in body)
            {
                if (string.IsNullOrWhiteSpace(entry.Name) ||
                    !ConnectionRegistry.KnownEngines.Contains(entry.Engine))
                {
                    skipped.Add(new { entry.Name, reason = $"unknown engine '{entry.Engine}'" });
                    continue;
                }

                // Enough of a connection string to identify the target; the user fills in the rest.
                var draft = string.Join(";", new[]
                {
                    entry.Host is { Length: > 0 } ? $"Host={entry.Host}" : null,
                    entry.Database is { Length: > 0 } ? $"Database={entry.Database}" : null,
                }.Where(p => p is not null));

                try
                {
                    store.Add(new ConnectionSpec("", entry.Name.Trim(), entry.Engine,
                        draft.Length > 0 ? draft : " ", entry.ReadOnly, entry.Color, entry.Group,
                        ConnectionSource.Stored));
                    imported.Add(entry.Name);
                }
                catch (InvalidOperationException e)
                {
                    // One duplicate name must not abort the rest of the file.
                    skipped.Add(new { entry.Name, reason = e.Message });
                }
            }

            return Results.Ok(new { imported, skipped });
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

    public record PortableConnection(string Name, string Engine, bool ReadOnly,
        string? Color, string? Group, string? Host, string? Database, bool NeedsCredentials);

    /// Export carries definitions, never secrets: no connection string, no password, no key. The
    /// importing side has to supply credentials, which is the point of a shareable file.
    private static PortableConnection ToPortable(ConnectionSpec spec)
    {
        var parts = spec.ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);

        string? Value(params string[] keys) =>
            keys.Select(k => parts.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => v is not null);

        return new PortableConnection(spec.Name, spec.Engine, spec.ReadOnly, spec.Color, spec.Group,
            Value("Host", "Server", "Data Source"), Value("Database", "Initial Catalog"), true);
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
