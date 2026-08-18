using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class SchemaEndpoints
{
    public static void MapSchemaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/drivers", (DriverRegistry drivers) =>
            Results.Ok(drivers.All().Select(d => new { d.Info, d.Caps })));

        app.MapGet("/api/schema/{conn}", async (string conn, string? parent,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var parentRef = string.IsNullOrEmpty(parent) ? null : SchemaNodeRef.Parse(parent);
                    var nodes = await driver.IntrospectAsync(session, parentRef, ct);
                    return Results.Ok(nodes.Select(n => new
                    {
                        @ref = n.Ref.ToString(),
                        kind = n.Ref.Kind.ToString(),
                        label = n.Label,
                        hasChildren = n.HasChildren,
                        detail = n.Detail,
                    }));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/schema/{conn}/object/{objectRef}", async (string conn, string objectRef,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await driver.DescribeAsync(session, ParseObjectRef(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    /// Routing decodes every percent-escape in a route value except %2F, which stays encoded so a
    /// slash cannot silently split a segment. Object references contain slashes, so put them back —
    /// and only them, since decoding the whole value again would corrupt a name containing a literal
    /// percent sign.
    internal static SchemaNodeRef ParseObjectRef(string value) =>
        SchemaNodeRef.Parse(value.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase));
}
