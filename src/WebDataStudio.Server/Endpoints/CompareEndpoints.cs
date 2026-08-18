using WebDataStudio.Server.Compare;
using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class CompareEndpoints
{
    public record SchemaCompareRequest(
        string SourceConnectionId, string? SourceSchema,
        string TargetConnectionId, string? TargetSchema);

    public record DataCompareRequest(
        string SourceConnectionId, string SourceRef,
        string TargetConnectionId, string TargetRef,
        List<string> KeyColumns, int? MaxRows);

    public static void MapCompareEndpoints(this WebApplication app)
    {
        app.MapPost("/api/compare/schema", async (SchemaCompareRequest body, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (sourceDriver, sourceSession) = await factory.OpenAsync(body.SourceConnectionId, ct);
                await using (sourceSession)
                {
                    var (targetDriver, targetSession) = await factory.OpenAsync(body.TargetConnectionId, ct);
                    await using (targetSession)
                    {
                        var source = await ReadSchemaAsync(sourceDriver, sourceSession, body.SourceSchema, ct);
                        var target = await ReadSchemaAsync(targetDriver, targetSession, body.TargetSchema, ct);

                        var comparison = SchemaComparer.Compare(source, target);
                        var writer = DdlEndpoints.WriterFor(targetDriver.Info.Id);

                        var statements = writer is null
                            ? []
                            : SchemaComparer.SyncScript(comparison, source, target, writer);

                        return Results.Ok(new
                        {
                            comparison.TablesOnlyInSource,
                            comparison.TablesOnlyInTarget,
                            changedTables = comparison.ChangedTables.Select(c => new
                            {
                                c.Name,
                                addedColumns = c.Change.AddedColumns.Select(x => x.Name),
                                droppedColumns = c.Change.DroppedColumns.Select(x => x.Name),
                                alteredColumns = c.Change.AlteredColumns.Select(x => x.Column.Name),
                            }),
                            comparison.IdenticalTables,
                            // Written in the target's dialect: the script runs against the target.
                            script = string.Join("\n", statements.Select(s => s.Sql)),
                            destructive = statements.Any(s => s.Destructive),
                            writerAvailable = writer is not null,
                        });
                    }
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/compare/data", async (DataCompareRequest body, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (sourceDriver, sourceSession) = await factory.OpenAsync(body.SourceConnectionId, ct);
                await using (sourceSession)
                {
                    var (targetDriver, targetSession) = await factory.OpenAsync(body.TargetConnectionId, ct);
                    await using (targetSession)
                    {
                        var sourceRef = SchemaEndpoints.ParseObjectRef(body.SourceRef);
                        var targetRef = SchemaEndpoints.ParseObjectRef(body.TargetRef);

                        var keys = body.KeyColumns.Count > 0
                            ? body.KeyColumns
                            : await PrimaryKeyAsync(sourceDriver, sourceSession, sourceRef, ct);

                        var comparison = await DataComparer.CompareAsync(
                            sourceDriver, sourceSession, targetDriver, targetSession,
                            sourceRef, targetRef, keys, body.MaxRows ?? 100_000, ct);

                        var script = DataComparer.SyncScript(comparison, targetRef, keys, targetDriver.Dialect);

                        return Results.Ok(new
                        {
                            keyColumns = keys,
                            comparison.Columns,
                            missing = comparison.Missing,
                            extra = comparison.Extra,
                            different = comparison.Different,
                            comparison.Identical,
                            comparison.Truncated,
                            script = string.Join("\n", script),
                        });
                    }
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    internal static async Task<List<TableDefinition>> ReadSchemaAsync(IDbDriver driver, IDbSession session,
        string? schema, CancellationToken ct)
    {
        var tables = new List<TableDefinition>();
        var queue = new Queue<SchemaNodeRef?>();
        queue.Enqueue(null);
        var visited = 0;

        while (queue.Count > 0 && visited++ < 200)
        {
            var parent = queue.Dequeue();
            foreach (var node in await driver.IntrospectAsync(session, parent, ct))
            {
                if (node.Ref.Kind == SchemaNodeKind.Table)
                {
                    if (schema is { Length: > 0 }
                        && !node.Ref.Path[0].Equals(schema, StringComparison.OrdinalIgnoreCase))
                        continue;

                    tables.Add(TableDefinition.From(await driver.DescribeAsync(session, node.Ref, ct)));
                    continue;
                }

                if (node.HasChildren && node.Ref.Kind is not (SchemaNodeKind.Table or SchemaNodeKind.View))
                    queue.Enqueue(node.Ref);
            }
        }

        return tables;
    }

    private static async Task<List<string>> PrimaryKeyAsync(IDbDriver driver, IDbSession session,
        SchemaNodeRef target, CancellationToken ct)
    {
        var detail = await driver.DescribeAsync(session, target, ct);
        var identity = Editing.RowIdentity.Resolve(detail);

        return identity.Editable
            ? identity.KeyColumns.ToList()
            : throw new InvalidOperationException(
                "this table has no primary key; name the columns to compare by");
    }
}
