using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace WebDataStudio.Server.Endpoints;

public static class SchemaEndpoints
{
    public record GrantRequest(string Grantee, string Privilege, bool? Revoke);

    public static void MapSchemaEndpoints(this WebApplication app)
    {
        // What the engine knows about one table beyond its shape: how big it is, how much of it is
        // dead, when it was last vacuumed, and which of its indexes anybody reads. The questions
        // before "should I add an index" and "why is this table 40 GB".
        app.MapGet("/api/schema/{conn}/statistics", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await ObjectStatisticsReader.ReadAsync(
                        driver, session, ParseObjectRef(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // Who may do what to this object. "Who can see this table" is a question people answer by
        // guessing far too often.
        app.MapGet("/api/schema/{conn}/privileges", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await ObjectPrivilegeReader.ReadAsync(
                        driver, session, ParseObjectRef(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // The statement that changes a grant — handed over, not run: it goes through the same
        // script preview as anything else that changes a database.
        app.MapPost("/api/schema/{conn}/privileges/statement", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, GrantRequest body, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (string.IsNullOrWhiteSpace(body.Grantee))
                        return Results.BadRequest(new { message = "a grantee is required" });

                    return Results.Ok(new
                    {
                        sql = ObjectPrivilegeReader.Statement(driver, ParseObjectRef(objectRef),
                            body.Grantee.Trim(), body.Privilege, body.Revoke == true),
                    });
                }
            }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // What moved since the last snapshot, for anybody who wants to know why a query stopped
        // working. Absent unless WDS_SCHEMA_SNAPSHOT_DIR is set: without it there is nothing to
        // compare against.
        app.MapGet("/api/schema/{conn}/drift", (string conn, SchemaSnapshots snapshots) =>
            !snapshots.Configured
                ? Results.Ok(new { configured = false, drift = (object?)null })
                : Results.Ok(new
                {
                    configured = true,
                    drift = snapshots.DriftOf(conn) is { } drift
                        ? new
                        {
                            before = drift.Before,
                            after = drift.After,
                            summary = drift.Summary,
                            drift.Added,
                            drift.Removed,
                            drift.Changed,
                        }
                        : null,
                }));

        // Takes one now rather than waiting for the next start — the button behind "did my
        // migration do what I think it did".
        app.MapPost("/api/schema/snapshot", async (SchemaSnapshots snapshots, CancellationToken ct) =>
            snapshots.Configured
                ? Results.Ok(new { moved = await snapshots.SweepAsync(ct) })
                : Results.BadRequest(new
                {
                    message = "no snapshot directory is configured; set WDS_SCHEMA_SNAPSHOT_DIR",
                }));

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

                    // Every driver stops at the object itself. Its columns, indexes, keys and
                    // triggers are already in DescribeAsync, so the tree grows one level deeper
                    // here rather than in nine drivers.
                    if (nodes.Count == 0 && parentRef is { Kind: SchemaNodeKind.Table
                            or SchemaNodeKind.View or SchemaNodeKind.MaterializedView })
                        nodes = ObjectChildren(parentRef, await driver.DescribeAsync(session, parentRef, ct));
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

        // The object reference travels in the query string, not the path: it contains a slash
        // ("Table:dbo/AbpUsers"), and the reverse proxy in front of a deployed studio — Envoy on
        // Azure Container Apps, and most others — decodes %2F back to a real slash before routing.
        // The route then no longer matches and every object lookup answered 404 in the cloud while
        // working on a machine with nothing in front of it.
        app.MapGet("/api/schema/{conn}/object", async (string conn, [FromQuery(Name = "ref")] string objectRef,
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
    /// The parts of an object as tree nodes: columns first, then indexes, foreign keys and
    /// triggers. The reference of each carries its parent's path, so an action knows the table.
    private static IReadOnlyList<SchemaNode> ObjectChildren(SchemaNodeRef parent, ObjectDetail detail)
    {
        SchemaNodeRef Child(SchemaNodeKind kind, string name) =>
            new(kind, [.. parent.Path, name]);

        var nodes = new List<SchemaNode>();

        nodes.AddRange(detail.Columns.OrderBy(c => c.Position).Select(column => new SchemaNode(
            Child(SchemaNodeKind.Column, column.Name),
            column.Name, false,
            $"{column.DataType}{(column.Nullable ? "" : " not null")}{(column.IsPrimaryKey ? " · pk" : "")}")));

        nodes.AddRange(detail.Indexes.Select(index => new SchemaNode(
            Child(SchemaNodeKind.Index, index.Name),
            index.Name, false,
            string.Join(", ", index.Columns)
            + (index.Primary ? " · primary" : index.Unique ? " · unique" : "")
            + (index.FullText ? " · full text" : ""))));

        nodes.AddRange(detail.ForeignKeys.Select(key => new SchemaNode(
            Child(SchemaNodeKind.ForeignKey, key.Name),
            key.Name, false,
            $"{string.Join(", ", key.Columns)} → {key.ReferencedTable}")));

        nodes.AddRange(detail.Triggers.Select(trigger => new SchemaNode(
            Child(SchemaNodeKind.Trigger, trigger.Name),
            trigger.Name, false, $"{trigger.Timing} {trigger.Event}")));

        return nodes;
    }

    internal static SchemaNodeRef ParseObjectRef(string value) =>
        SchemaNodeRef.Parse(value.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase));
}
