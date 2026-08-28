using System.Data.Common;
using Microsoft.Extensions.Caching.Memory;
using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace WebDataStudio.Server.Endpoints;

public static class DdlEndpoints
{
    public record PreviewRequest(string? ObjectRef, TableDefinition After);
    public record ApplyRequest(string Hash);

    public record ScriptPreviewRequest(string Sql);

    /// Anything that removes or rewrites, so the confirmation can say so. Deliberately blunt: a
    /// false positive costs a red line of text, a false negative costs data.
    private static bool IsDestructive(string sql) =>
        new[] { "DROP", "TRUNCATE", "DELETE", "ALTER TABLE" }
            .Any(word => sql.TrimStart().StartsWith(word, StringComparison.OrdinalIgnoreCase));

    /// The first two words, which is what a statement is called in a list of statements.
    private static string Describe(string sql)
    {
        var words = sql.TrimStart().Split([' ', '\n', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Take(2)).ToUpperInvariant();
    }
    public record RenameRequest(string ObjectRef, string NewName);
    public record RoutineRequest(string Schema, string Name, string Kind, string Body);
    public record ViewRequest(string Schema, string Name, string Select);
    public record SequenceRequest(string Schema, string Name, bool Create, long? Start, long? Increment,
        long? MinValue, long? MaxValue, bool? Cycle, long? Cache, long? RestartWith);
    public record SchemaRequest(string Name, bool Drop, bool? Cascade);
    public record CommentRequest(string ObjectRef, string? Text);
    public record TriggerStateRequest(string ObjectRef, bool Enabled);
    public record DropRequest(string ObjectRef);

    /// Writers are stateless; one per engine is enough.
    private static readonly Dictionary<string, DdlWriterBase> Writers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgresql"] = new PostgreSqlDdlWriter(),
        ["mysql"] = new MySqlDdlWriter(),
        ["sqlserver"] = new SqlServerDdlWriter(),
        ["sqlite"] = new SqliteDdlWriter(),
    };

    public static DdlWriterBase? WriterFor(string engine) =>
        Writers.TryGetValue(engine, out var writer) ? writer : null;

    public static void MapDdlEndpoints(this WebApplication app)
    {
        // The object reference travels in the query string, not the path: it contains a slash
        // ("Table:dbo/AbpUsers"), and the reverse proxy in front of a deployed studio — Envoy on
        // Azure Container Apps, and most others — decodes %2F back to a real slash before routing.
        // The route then no longer matches and every object lookup answered 404 in the cloud while
        // working on a machine with nothing in front of it.
        app.MapGet("/api/ddl/{conn}", async (string conn, [FromQuery(Name = "ref")] string objectRef,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var detail = await driver.DescribeAsync(session, target, ct);
                    var definition = TableDefinition.From(detail);
                    var writer = WriterFor(driver.Info.Id);

                    return Results.Ok(new
                    {
                        definition,
                        // Engines that keep the original text hand it over; the rest get a generated one.
                        create = detail.Ddl ?? (writer is null
                            ? null
                            : string.Join("\n", writer.CreateTable(definition).Select(s => s.Sql))),
                        supported = writer is not null,
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/ddl/{conn}/dependencies", async (string conn, [FromQuery(Name = "ref")] string objectRef,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await DependencyFinder.FindAsync(driver, session,
                        SchemaEndpoints.ParseObjectRef(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/ddl/{conn}/preview", async (string conn, PreviewRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var writer = WriterFor(driver.Info.Id);
                    if (writer is null)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} has no schema writer yet",
                        });

                    IReadOnlyList<DdlStatement> statements;

                    if (body.ObjectRef is { Length: > 0 })
                    {
                        var target = SchemaEndpoints.ParseObjectRef(body.ObjectRef);
                        var before = TableDefinition.From(await driver.DescribeAsync(session, target, ct));

                        // The designer speaks neutral types ("int"); the database answers in its own
                        // ("INTEGER"). Map both sides before diffing, or every save looks like a
                        // type change — and on SQLite that means a full table rebuild for nothing.
                        var change = TableDiff.Compute(MapTypes(before, writer), MapTypes(body.After, writer));

                        if (change.IsEmpty)
                            return Results.BadRequest(new { message = "there is nothing to change" });

                        statements = writer.AlterTable(before, change);
                    }
                    else
                    {
                        statements = writer.CreateTable(body.After);
                    }

                    var hash = Hash(statements);
                    cache.Set($"ddl:{hash}", statements, TimeSpan.FromMinutes(10));

                    return Results.Ok(new
                    {
                        hash,
                        statements = statements.Select(s => new { s.Sql, s.Destructive, s.Description }),
                        script = string.Join("\n", statements.Select(s => s.Sql)),
                        destructive = statements.Any(s => s.Destructive),
                        transactional = driver.Caps.Transactions && driver.Info.Id != "mysql",
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // A finding from the health report names its own fix ("CREATE INDEX …"). This turns that
        // statement into the same previewed, hashed change every other write goes through, so
        // applying a recommendation is not a second, unreviewed path into the database.
        app.MapPost("/api/ddl/{conn}/script/preview", async (string conn, ScriptPreviewRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Sql))
                return Results.BadRequest(new { message = "there is no statement to preview" });

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var statements = StatementSplitter
                        .Split(body.Sql, driver.Dialect)
                        .Select(statement => new DdlStatement(
                            statement.Text, IsDestructive(statement.Text), Describe(statement.Text)))
                        .ToList();

                    if (statements.Count == 0)
                        return Results.BadRequest(new { message = "there is no statement to preview" });

                    var hash = Hash(statements);
                    cache.Set($"ddl:{hash}", (IReadOnlyList<DdlStatement>)statements, TimeSpan.FromMinutes(10));

                    return Results.Ok(new
                    {
                        hash,
                        statements = statements.Select(s => new { s.Sql, s.Destructive, s.Description }),
                        script = string.Join("\n", statements.Select(s => s.Sql)),
                        destructive = statements.Any(s => s.Destructive),
                        transactional = driver.Caps.Transactions && driver.Info.Id != "mysql",
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/ddl/{conn}/apply", async (string conn, ApplyRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            if (cache.Get($"ddl:{body.Hash}") is not IReadOnlyList<DdlStatement> statements)
                return Results.Json(
                    new { message = "the preview expired; preview again before applying" },
                    statusCode: StatusCodes.Status409Conflict);

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Spec.ReadOnly)
                        return Results.Json(new { message = "this connection is read-only" },
                            statusCode: StatusCodes.Status403Forbidden);

                    // MySQL commits DDL implicitly, so a rollback there would be a lie. Engines with
                    // transactional DDL get a real transaction; the rest report what already ran.
                    var transactional = driver.Caps.Transactions && driver.Info.Id != "mysql";
                    DbTransaction? transaction = transactional
                        ? await session.Connection.BeginTransactionAsync(ct)
                        : null;

                    var executed = new List<string>();

                    try
                    {
                        foreach (var statement in statements)
                        {
                            await using var command = session.Connection.CreateCommand();
                            command.CommandText = statement.Sql;
                            command.Transaction = transaction;
                            await command.ExecuteNonQueryAsync(ct);
                            executed.Add(statement.Description);
                        }

                        if (transaction is not null) await transaction.CommitAsync(ct);
                        cache.Remove($"ddl:{body.Hash}");

                        return Results.Ok(new { applied = executed.Count, executed, partiallyApplied = false });
                    }
                    catch (DbException e)
                    {
                        if (transaction is not null) await transaction.RollbackAsync(ct);

                        return Results.Json(new
                        {
                            message = e.Message,
                            applied = transactional ? 0 : executed.Count,
                            executed,
                            partiallyApplied = !transactional && executed.Count > 0,
                        }, statusCode: 400);
                    }
                    finally
                    {
                        if (transaction is not null) await transaction.DisposeAsync();
                    }
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/ddl/{conn}/rename", async (string conn, RenameRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var writer = WriterFor(driver.Info.Id);
                    if (writer is null) return Results.BadRequest(new { message = "no schema writer for this engine" });

                    var target = SchemaEndpoints.ParseObjectRef(body.ObjectRef);
                    var dependencies = await DependencyFinder.FindAsync(driver, session, target, ct);
                    var statements = writer.Rename(target, body.NewName);
                    var hash = Hash(statements);
                    cache.Set($"ddl:{hash}", statements, TimeSpan.FromMinutes(10));

                    return Results.Ok(new
                    {
                        hash,
                        script = string.Join("\n", statements.Select(s => s.Sql)),
                        dependencies,
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // A view, written the way the engine spells "replace this definition".
        app.MapPost("/api/ddl/{conn}/view", (string conn, ViewRequest body, SessionFactory factory,
            IMemoryCache cache, CancellationToken ct) =>
            ScriptAsync(conn, factory, cache, ct,
                (writer, _) => writer.CreateOrReplaceView(body.Schema, body.Name, body.Select)));

        // A sequence created, or changed — including the restart that follows an import which
        // wrote its own ids.
        app.MapPost("/api/ddl/{conn}/sequence", (string conn, SequenceRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
            ScriptAsync(conn, factory, cache, ct, (writer, _) =>
            {
                var definition = new SequenceDefinition(body.Schema, body.Name, body.Start,
                    body.Increment, body.MinValue, body.MaxValue, body.Cycle == true, body.Cache,
                    body.RestartWith);

                return body.Create ? writer.CreateSequence(definition) : writer.AlterSequence(definition);
            }));

        app.MapPost("/api/ddl/{conn}/schema", (string conn, SchemaRequest body, SessionFactory factory,
            IMemoryCache cache, CancellationToken ct) =>
            ScriptAsync(conn, factory, cache, ct, (writer, _) => body.Drop
                ? writer.DropSchema(body.Name, body.Cascade == true)
                : writer.CreateSchema(body.Name)));

        // The description the database itself keeps — what another tool reading this database sees.
        // The studio's own notes are the other half, and they need no rights at all.
        app.MapPost("/api/ddl/{conn}/comment", (string conn, CommentRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
            ScriptAsync(conn, factory, cache, ct,
                (writer, _) => writer.Comment(SchemaEndpoints.ParseObjectRef(body.ObjectRef), body.Text)));

        // A trigger stopped rather than dropped: the definition stays, the firing does not.
        app.MapPost("/api/ddl/{conn}/trigger", (string conn, TriggerStateRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
            ScriptAsync(conn, factory, cache, ct, (writer, _) => writer.SetTriggerEnabled(
                SchemaEndpoints.ParseObjectRef(body.ObjectRef), body.Enabled)));

        // Dropping anything the tree shows, the same way a table is dropped: previewed, with
        // whatever depends on it listed first.
        app.MapPost("/api/ddl/{conn}/drop", async (string conn, DropRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var writer = WriterFor(driver.Info.Id);
                    if (writer is null) return Results.BadRequest(new { message = NoWriter(driver) });

                    var target = SchemaEndpoints.ParseObjectRef(body.ObjectRef);
                    var dependencies = await DependencyFinder.FindAsync(driver, session, target, ct);
                    var statements = writer.DropObject(target);
                    var hash = Hash(statements);
                    cache.Set($"ddl:{hash}", statements, TimeSpan.FromMinutes(10));

                    return Results.Ok(new
                    {
                        hash,
                        script = string.Join("\n", statements.Select(s => s.Sql)),
                        dependencies,
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // A procedure, a function or a trigger, from the source in the editor. The engine decides
        // whether that is CREATE OR REPLACE, CREATE OR ALTER, or a drop and a create.
        app.MapPost("/api/ddl/{conn}/routine", (string conn, RoutineRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
            ScriptAsync(conn, factory, cache, ct, (writer, _) => writer.CreateOrReplaceRoutine(
                new RoutineDefinition(body.Schema, body.Name, body.Kind, body.Body))));
    }

    private static string NoWriter(IDbDriver driver) =>
        $"the studio writes DDL for PostgreSQL, MySQL, SQL Server and SQLite; {driver.Info.Label} " +
        "takes a statement in a query tab";

    /// Every "build me this statement" endpoint has the same shape: open the connection, ask the
    /// engine's writer, cache what it said under a hash the apply endpoint reads back. Nothing here
    /// runs anything — that is a second, deliberate call.
    private static async Task<IResult> ScriptAsync(string conn, SessionFactory factory,
        IMemoryCache cache, CancellationToken ct,
        Func<DdlWriterBase, IDbSession, IReadOnlyList<DdlStatement>> build)
    {
        try
        {
            var (driver, session) = await factory.OpenAsync(conn, ct);
            await using (session)
            {
                var writer = WriterFor(driver.Info.Id);
                if (writer is null) return Results.BadRequest(new { message = NoWriter(driver) });

                var statements = build(writer, session);
                var hash = Hash(statements);
                cache.Set($"ddl:{hash}", statements, TimeSpan.FromMinutes(10));

                return Results.Ok(new
                {
                    hash,
                    script = string.Join("\n", statements.Select(s => s.Sql)),
                    // What the person is about to do that cannot be undone by doing it again.
                    destructive = statements.Any(s => s.Destructive),
                });
            }
        }
        catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
        // What an engine cannot do is a sentence for a person, not a 500.
        catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
    }

    private static TableDefinition MapTypes(TableDefinition table, DdlWriterBase writer) =>
        table with
        {
            Columns = table.Columns
                .Select(c => c with { Type = writer.MapType(c.Type) })
                .ToList(),
        };

    private static string Hash(IEnumerable<DdlStatement> statements) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", statements.Select(s => s.Sql)))))
            .ToLowerInvariant();
}
