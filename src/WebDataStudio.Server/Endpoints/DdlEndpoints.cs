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
    public record RenameRequest(string ObjectRef, string NewName);
    public record RoutineRequest(string Schema, string Name, string Kind, string Body);

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

        app.MapPost("/api/ddl/{conn}/routine", async (string conn, RoutineRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var writer = WriterFor(driver.Info.Id);
                    if (writer is null) return Results.BadRequest(new { message = "no schema writer for this engine" });

                    var statements = writer.CreateOrReplaceRoutine(
                        new RoutineDefinition(body.Schema, body.Name, body.Kind, body.Body));

                    var hash = Hash(statements);
                    cache.Set($"ddl:{hash}", statements, TimeSpan.FromMinutes(10));

                    return Results.Ok(new { hash, script = string.Join("\n", statements.Select(s => s.Sql)) });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
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
