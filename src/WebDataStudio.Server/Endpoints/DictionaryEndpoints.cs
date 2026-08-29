using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// The document somebody asks for when they join the team.
public static class DictionaryEndpoints
{
    public static void MapDictionaryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dictionary/{conn}", async (string conn, string? schema, int? limit,
            SessionFactory factory, WorkspaceStore workspace, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var tables = await TablesAsync(driver, session, schema, ct);

                    // Read once rather than per table: a hundred tables would otherwise be a
                    // hundred queries against the workspace for the notes nobody wrote.
                    var notes = workspace.ListNotes(conn, null, 2_000)
                        .GroupBy(note => note.ObjectRef)
                        .ToDictionary(group => group.Key,
                            group => (IReadOnlyList<string>)group
                                .OrderBy(note => note.At)
                                .Select(note => $"{note.Body} — {note.Author}")
                                .ToList());

                    var title = schema is { Length: > 0 }
                        ? $"{session.Spec.Name} · {schema}"
                        : session.Spec.Name;

                    var markdown = await DataDictionary.WriteAsync(driver, session, tables,
                        objectRef => notes.GetValueOrDefault(objectRef, []),
                        title, limit ?? DataDictionary.DefaultLimit, ct);

                    return Results.Text(markdown, "text/markdown");
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    /// Every table of one schema, or of all of them. The tree is walked rather than queried
    /// directly, because each driver already knows what a level of it looks like.
    private static async Task<List<SchemaNodeRef>> TablesAsync(IDbDriver driver, IDbSession session,
        string? schema, CancellationToken ct)
    {
        var tables = new List<SchemaNodeRef>();
        var roots = await driver.IntrospectAsync(session, null, ct);

        foreach (var root in roots)
        {
            if (schema is { Length: > 0 } && !root.Ref.Name.Equals(schema, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var child in await driver.IntrospectAsync(session, root.Ref, ct))
            {
                if (child.Ref.Kind is SchemaNodeKind.Table or SchemaNodeKind.MaterializedView)
                {
                    tables.Add(child.Ref);
                    continue;
                }

                // Some engines put tables one level further down, under a folder.
                if (child.Ref.Kind is SchemaNodeKind.TableFolder or SchemaNodeKind.Schema)
                {
                    foreach (var leaf in await driver.IntrospectAsync(session, child.Ref, ct))
                    {
                        if (leaf.Ref.Kind is SchemaNodeKind.Table or SchemaNodeKind.MaterializedView)
                            tables.Add(leaf.Ref);
                    }
                }
            }
        }

        return tables;
    }
}
