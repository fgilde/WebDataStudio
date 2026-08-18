using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// The ER diagram is just the schema read once and reshaped into nodes and edges — the layout
/// itself happens in the browser, where the user can move things around.
public static class DiagramEndpoints
{
    public static void MapDiagramEndpoints(this WebApplication app)
    {
        app.MapGet("/api/diagram/{conn}", async (string conn, string? schema, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var tables = await CompareEndpoints.ReadSchemaAsync(driver, session, schema, ct);
                    var names = tables.Select(Qualified).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var nodes = tables.Select(t => new
                    {
                        id = Qualified(t),
                        t.Schema,
                        t.Name,
                        columns = t.Columns.Select(c => new
                        {
                            c.Name,
                            c.Type,
                            c.Nullable,
                            primaryKey = t.Constraints.Any(x =>
                                x.Kind == ConstraintKind.PrimaryKey &&
                                x.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)),
                            foreignKey = t.Constraints.Any(x =>
                                x.Kind == ConstraintKind.ForeignKey &&
                                x.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)),
                        }),
                    });

                    var edges = tables.SelectMany(t => t.Constraints
                        .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null)
                        .Select(c => new
                        {
                            c.Name,
                            source = Qualified(t),
                            target = Resolve(c.ReferencedTable!, t.Schema),
                            sourceColumns = c.Columns,
                            targetColumns = c.ReferencedColumns ?? [],
                            // An edge whose target was filtered out by the schema filter still
                            // exists; the UI draws it dangling rather than hiding the relation.
                            resolved = names.Contains(Resolve(c.ReferencedTable!, t.Schema)),
                        }));

                    return Results.Ok(new { nodes, edges });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        static string Qualified(TableDefinition table) =>
            table.Schema is { Length: > 0 } ? $"{table.Schema}.{table.Name}" : table.Name;

        // A referenced table may or may not carry its schema; unqualified means "same schema".
        static string Resolve(string referenced, string schema) =>
            referenced.Contains('.') || schema.Length == 0 ? referenced : $"{schema}.{referenced}";
    }
}
