using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace WebDataStudio.Server.Endpoints;

public static class SchemaEndpoints
{
    public record GrantRequest(string Grantee, string Privilege, bool? Revoke);

    public record BulkGrantRequest(
        string Schema, string Grantee, string[] Privileges, bool? Revoke, bool? IncludeFuture);

    public record PolicyRequest(
        string Name, string? Command, string? Roles, string? Using, string? Check, bool? Drop);

    public record SecurityRequest(bool Enable, bool? Force);

    public record PartitionRequest(string Partition, string? Bound, bool? Detach, bool? Concurrently);

    public record RefreshRequest(bool? Concurrently);

    public record TrialRunRequest(List<string?>? Arguments);

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

        // What a function is: its source, its parameters, what it returns. Not a debugger — see
        // FunctionInspector for what a "trial run" is and is not.
        app.MapGet("/api/schema/{conn}/function", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await FunctionInspector.ReadAsync(
                        driver, session, ParseObjectRef(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // Runs it and rolls the transaction back. Refused on a read-only connection: a rollback
        // undoes what the transaction held, and a function can do things a transaction does not.
        app.MapPost("/api/schema/{conn}/function/run", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, TrialRunRequest body, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Spec.ReadOnly)
                        return Results.Json(new
                        {
                            message = "this connection is read-only, and a trial run still runs the function",
                        }, statusCode: StatusCodes.Status403Forbidden);

                    return Results.Ok(await FunctionInspector.RunAsync(
                        driver, session, ParseObjectRef(objectRef), body.Arguments ?? [], ct));
                }
            }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // Row-level security: whether it is on, and what the policies say. PostgreSQL-only,
        // because it is a PostgreSQL feature — everything else says "not supported" rather than
        // showing an empty list that reads like "no policies".
        app.MapGet("/api/schema/{conn}/policies", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await ObjectAdmin.ReadSecurityAsync(
                        driver, session, ParseObjectRef(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/schema/{conn}/policies/statement", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, PolicyRequest body, SessionFactory factory,
            CancellationToken ct) => await StatementAsync(conn, factory, ct, (driver, target) =>
                ObjectAdmin.PolicyStatement(driver, target, body.Name, body.Command, body.Roles,
                    body.Using, body.Check, body.Drop == true), objectRef));

        app.MapPost("/api/schema/{conn}/policies/security-statement", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SecurityRequest body, SessionFactory factory,
            CancellationToken ct) => await StatementAsync(conn, factory, ct, (driver, target) =>
                ObjectAdmin.SecurityStatement(driver, target, body.Enable, body.Force == true), objectRef));

        // How a partitioned table is cut up, and what each piece costs.
        app.MapGet("/api/schema/{conn}/partitions", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await ObjectAdmin.ReadPartitionsAsync(
                        driver, session, ParseObjectRef(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/schema/{conn}/partitions/statement", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, PartitionRequest body, SessionFactory factory,
            CancellationToken ct) => await StatementAsync(conn, factory, ct, (driver, target) =>
                ObjectAdmin.PartitionStatement(driver, target, body.Partition, body.Bound,
                    body.Detach == true, body.Concurrently == true), objectRef));

        // Refreshing a materialised view is a statement like any other, so it is previewed like one.
        app.MapPost("/api/schema/{conn}/refresh-statement", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, RefreshRequest body, SessionFactory factory,
            CancellationToken ct) => await StatementAsync(conn, factory, ct, (driver, target) =>
                ObjectAdmin.RefreshStatement(driver, target, body.Concurrently == true), objectRef));

        // "SELECT on everything in public for the reporting role" — one script rather than one
        // dialog per table.
        app.MapPost("/api/schema/{conn}/privileges/bulk-statement", async (string conn,
            BulkGrantRequest body, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    // PostgreSQL says "ALL TABLES IN SCHEMA" in one statement; the others need the
                    // list, so it is read here rather than guessed at in the browser.
                    var tables = driver.Info.Id == "postgresql"
                        ? []
                        : await TablesOfAsync(driver, session, body.Schema, ct);

                    return Results.Ok(new
                    {
                        sql = ObjectAdmin.BulkGrantStatement(driver, body.Schema, body.Grantee,
                            body.Privileges ?? [], tables, body.Revoke == true,
                            body.IncludeFuture == true),
                        tables = tables.Count,
                    });
                }
            }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
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

        // "What do I run on the other machine": the statements that would carry a database from
        // the snapshot's schema to this one. Nothing runs here — it lands in a query tab, and the
        // apply endpoint is the one that executes.
        app.MapGet("/api/schema/{conn}/drift/script", async (string conn, SchemaSnapshots snapshots,
            SessionFactory factory, CancellationToken ct) =>
        {
            if (!snapshots.Configured)
                return Results.BadRequest(new
                {
                    message = "no snapshot directory is configured; set WDS_SCHEMA_SNAPSHOT_DIR",
                });

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var writer = DdlEndpoints.WriterFor(driver.Info.Id);

                    if (writer is null)
                        return Results.BadRequest(new
                        {
                            message = $"the studio writes no DDL for {driver.Info.Label}",
                        });

                    var before = snapshots.Saved(conn);
                    var after = await snapshots.TakeAsync(conn, conn, ct);

                    var script = await DriftMigration.BuildAsync(driver, session, writer, before, after, ct);

                    return Results.Ok(new
                    {
                        before = before?.At,
                        script = script.Text,
                        script.Destructive,
                        needsAPerson = script.NeedsAPerson,
                        statements = script.Statements.Count,
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // Takes one now rather than waiting for the next start — the button behind "did my
        // migration do what I think it did".
        app.MapPost("/api/schema/snapshot", async (SchemaSnapshots snapshots, CancellationToken ct) =>
            snapshots.Configured
                ? Results.Ok(new { moved = await snapshots.SweepAsync(ct) })
                : Results.BadRequest(new
                {
                    message = "no snapshot directory is configured; set WDS_SCHEMA_SNAPSHOT_DIR",
                }));

        // "Find 4711 in any table." The object search says where a table is; this says where a value
        // is, server-side and type-aware — a number is compared as a number, and a column that
        // cannot hold the value is not scanned at all.
        app.MapGet("/api/search/{conn}/data", async (string conn, string value, string? schema,
            bool? exact, int? maxTables, int? timeoutSeconds, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.Sql)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} has no tables to search",
                        });

                    return Results.Ok(await DataSearch.RunAsync(driver, session, value, schema,
                        exact ?? false, maxTables ?? DataSearch.DefaultMaxTables,
                        timeoutSeconds ?? 30, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (OperationCanceledException) { return Results.NoContent(); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // Which schemas this connection reads, and the whole list to choose from. The deployment can
        // fix the scope with WDS_CONN_<NAME>_SCHEMAS, and then this only reports it.
        app.MapGet("/api/schema/{conn}/scope", async (string conn, SessionFactory factory,
            SchemaScope scope, ConnectionRegistry connections, CancellationToken ct) =>
        {
            var spec = connections.Find(conn);
            if (spec is null) return Results.NotFound(new { message = $"no connection '{conn}'" });

            var fixedByEnvironment = scope.FromEnvironment(spec.Name);

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var all = (await driver.IntrospectAsync(session, null, ct))
                        .Where(node => node.Ref.Kind is SchemaNodeKind.Schema or SchemaNodeKind.Database)
                        .Select(node => node.Label)
                        .ToList();

                    return Results.Ok(new
                    {
                        available = all,
                        chosen = scope.Chosen(spec.Id),
                        fixedByEnvironment,
                        editable = fixedByEnvironment.Count == 0,
                    });
                }
            }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPut("/api/schema/{conn}/scope", (string conn, string[] body, SchemaScope scope,
            ConnectionRegistry connections) =>
        {
            var spec = connections.Find(conn);
            if (spec is null) return Results.NotFound(new { message = $"no connection '{conn}'" });

            if (scope.FromEnvironment(spec.Name).Count > 0)
                return Results.BadRequest(new
                {
                    message = "this connection's schemas are fixed by WDS_CONN_"
                              + spec.Name.ToUpperInvariant() + "_SCHEMAS",
                });

            scope.Choose(spec.Id, body);
            return Results.Ok(new { chosen = body });
        });

        app.MapGet("/api/drivers", (DriverRegistry drivers) =>
            Results.Ok(drivers.All().Select(d => new { d.Info, d.Caps })));

        app.MapGet("/api/schema/{conn}", async (string conn, string? parent,
            SessionFactory factory, SchemaScope scope, ConnectionRegistry connections,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var parentRef = string.IsNullOrEmpty(parent) ? null : SchemaNodeRef.Parse(parent);
                    var nodes = await driver.IntrospectAsync(session, parentRef, ct);

                    // A server with five thousand tables should not make every studio pay for all of
                    // them. Where somebody named the schemas they work in, the rest is not listed.
                    if (parentRef is null && connections.Find(conn) is { } spec)
                        nodes = scope.Filter(new ConnectionSpecName(spec.Id, spec.Name), nodes);

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

    /// Every "give me the statement" endpoint has the same shape: open the connection, build the
    /// text, hand it back. Nothing here runs anything — the script preview does that.
    private static async Task<IResult> StatementAsync(string conn, SessionFactory factory,
        CancellationToken ct, Func<IDbDriver, SchemaNodeRef, string> build, string objectRef)
    {
        try
        {
            var (driver, session) = await factory.OpenAsync(conn, ct);
            await using (session)
                return Results.Ok(new { sql = build(driver, ParseObjectRef(objectRef)) });
        }
        catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
        catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
    }

    /// The tables of one schema, for the engines that grant one at a time.
    private static async Task<List<string>> TablesOfAsync(
        IDbDriver driver, IDbSession session, string schema, CancellationToken ct)
    {
        var nodes = await driver.IntrospectAsync(session,
            new SchemaNodeRef(SchemaNodeKind.TableFolder, [schema, "tables"]), ct);

        return [.. nodes
            .Where(node => node.Ref.Kind == SchemaNodeKind.Table)
            .Select(node => node.Ref.Name)];
    }

    internal static SchemaNodeRef ParseObjectRef(string value) =>
        SchemaNodeRef.Parse(value.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase));
}
