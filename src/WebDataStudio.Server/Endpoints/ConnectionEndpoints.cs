using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class ConnectionEndpoints
{
    public record ConnectionRequest(string Name, string Engine, string ConnectionString,
        bool ReadOnly, string? Color, string? Group);

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
                    body.ConnectionString, body.ReadOnly, body.Color, body.Group, ConnectionSource.Stored));
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

        // P0 can only validate the shape; P1 replaces the body with a real driver probe.
        api.MapPost("/test", (ConnectionRequest body) =>
            Validate(body) ?? Results.Ok(new { ok = true, message = "connection definition is valid" }));
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
