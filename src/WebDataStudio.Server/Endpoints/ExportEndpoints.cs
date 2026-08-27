using System.Runtime.CompilerServices;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class ExportEndpoints
{
    public record ExportRequest(
        string ConnectionId, string? Sql, string? ObjectRef, string? Scope, string? Schema,
        int? MaxRows, ExportOptionsDto? Options, bool? IncludeSensitive = null);

    public record ExportOptionsDto(
        string? Delimiter, string? Encoding, bool? Header, string? NullText,
        string? DateFormat, bool? QuoteAll, string? TableName);

    // Only formats whose documents can hold several tables make sense for a whole schema.
    private static readonly string[] SchemaCapableFormats = ["sql-insert", "sql-create", "markdown", "html"];

    public static void MapExportEndpoints(this WebApplication app)
    {
        var defaultMaxRows = int.TryParse(app.Configuration["WDS_EXPORT_MAX_ROWS"], out var m) ? m : int.MaxValue;
        var timeout = int.TryParse(app.Configuration["WDS_QUERY_TIMEOUT_SECONDS"], out var t) ? t : 300;

        // The templates themselves: text with placeholders, listed, saved and deleted like any other
        // piece of workspace state. A template mounted by the deployment is read-only here.
        app.MapGet("/api/export/templates", (ExportTemplates templates) =>
            Results.Ok(new { templates = templates.All(), error = templates.Error }));

        app.MapPut("/api/export/templates", (ExportTemplate body, ExportTemplates templates) =>
        {
            if (string.IsNullOrWhiteSpace(body.Id) || string.IsNullOrWhiteSpace(body.Row))
                return Results.BadRequest(new { message = "a template needs an id and a row" });

            try
            {
                templates.Save(body);
                return Results.Ok(body);
            }
            catch (InvalidOperationException e) { return Results.BadRequest(new { message = e.Message }); }
        });

        app.MapDelete("/api/export/templates/{id}", (string id, ExportTemplates templates) =>
        {
            templates.Delete(id);
            return Results.NoContent();
        });

        app.MapGet("/api/export/formats", (ExporterRegistry registry) =>
            Results.Ok(registry.All()
                .OrderBy(e => e.Label)
                .Select(e => new
                {
                    format = e.Format,
                    label = e.Label,
                    extension = e.FileExtension,
                    contentType = e.ContentType,
                    supportsSchemaScope = SchemaCapableFormats.Contains(e.Format),
                })));

        app.MapPost("/api/export/{format}", async (string format, ExportRequest body, HttpContext ctx,
            ExporterRegistry registry, SessionFactory factory, ConnectionRegistry connections,
            MaskPolicyStore policies) =>
        {
            IResultExporter exporter;
            try { exporter = registry.Get(format); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }

            var scope = body.Scope ?? "result";
            if (scope == "schema" && !SchemaCapableFormats.Contains(format))
                return Results.BadRequest(new
                {
                    message = $"the {exporter.Label} format cannot hold a whole schema; " +
                              "export one table at a time or pick SQL, Markdown or HTML",
                });

            // A file leaves the building. Sensitive columns are masked in it unless the caller asks
            // for them on purpose — and on a connection marked as production (red) that ask is
            // refused outright, because "I exported prod's password column to my downloads folder"
            // is not a mistake anyone should be able to make in one click.
            var production = string.Equals(connections.Find(body.ConnectionId)?.Color, "red",
                StringComparison.OrdinalIgnoreCase);

            if (body.IncludeSensitive == true && production)
                return Results.BadRequest(new
                {
                    message = "this connection is marked as production; sensitive columns cannot be " +
                              "exported unmasked. Remove the production colour if that is really intended.",
                });

            var policy = body.IncludeSensitive == true
                ? new MaskPolicy(false, new HashSet<string>(), new HashSet<string>())
                : policies.For(body.ConnectionId);

            IDbDriver driver;
            IDbSession session;
            try
            {
                (driver, session) = await factory.OpenAsync(body.ConnectionId, ctx.RequestAborted);
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }

            await using (session)
            {
                List<(string Name, string Sql)> sources;
                try
                {
                    sources = await ResolveSourcesAsync(driver, session, body, scope, ctx.RequestAborted);
                }
                catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
                catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }

                if (sources.Count == 0)
                    return Results.BadRequest(new { message = "nothing to export" });

                var options = Resolve(body.Options, driver, sources[0].Name);
                var name = options.TableName ?? "result";
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm");

                ctx.Response.ContentType = exporter.ContentType;
                ctx.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"{Sanitize(name)}-{stamp}.{exporter.FileExtension}\"";

                var request = new ScriptRequest("", body.MaxRows ?? defaultMaxRows, timeout, body.Schema);

                if (exporter.RequiresSeekableStream)
                {
                    // Staged on disk rather than in memory: these writers seek, and a large export
                    // must not be bounded by RAM.
                    var temp = Path.Combine(Path.GetTempPath(), $"wds-export-{Guid.NewGuid():n}.tmp");
                    try
                    {
                        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite,
                            FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                        {
                            foreach (var source in sources)
                                await exporter.WriteAsync(file,
                                    Masking.Stream(
                                        driver.ExecuteAsync(session, request with { Sql = source.Sql }, ctx.RequestAborted),
                                        policy, ctx.RequestAborted),
                                    options with { TableName = source.Name }, ctx.RequestAborted);
                        }

                        await using var read = new FileStream(temp, FileMode.Open, FileAccess.Read,
                            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                        await read.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
                else
                {
                    foreach (var source in sources)
                        await exporter.WriteAsync(ctx.Response.Body,
                            Masking.Stream(
                                driver.ExecuteAsync(session, request with { Sql = source.Sql }, ctx.RequestAborted),
                                policy, ctx.RequestAborted),
                            options with { TableName = source.Name }, ctx.RequestAborted);
                }

                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                return Results.Empty;
            }
        });
    }

    /// One entry for a query or a single table, one per table for a whole schema.
    private static async Task<List<(string Name, string Sql)>> ResolveSourcesAsync(
        IDbDriver driver, IDbSession session, ExportRequest body, string scope, CancellationToken ct)
    {
        switch (scope)
        {
            case "table" when body.ObjectRef is not null:
            {
                var target = SchemaNodeRef.Parse(body.ObjectRef);
                return [(target.Name, $"SELECT * FROM {Qualify(driver, target)}")];
            }

            case "schema":
            {
                var tables = await FindTablesAsync(driver, session, body.Schema, ct);
                return tables.Select(t => (t.Name, $"SELECT * FROM {Qualify(driver, t)}")).ToList();
            }

            default:
                return body.Sql is { Length: > 0 } ? [(body.Options?.TableName ?? "result", body.Sql)] : [];
        }
    }

    private static async Task<List<SchemaNodeRef>> FindTablesAsync(
        IDbDriver driver, IDbSession session, string? schema, CancellationToken ct)
    {
        var found = new List<SchemaNodeRef>();
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
                    if (schema is null || node.Ref.Path[0].Equals(schema, StringComparison.OrdinalIgnoreCase))
                        found.Add(node.Ref);
                    continue;
                }
                if (node.HasChildren && node.Ref.Kind is not (SchemaNodeKind.Table or SchemaNodeKind.View))
                    queue.Enqueue(node.Ref);
            }
        }
        return found;
    }

    /// Schema-qualified where the engine has schemas, bare where it does not.
    private static string Qualify(IDbDriver driver, SchemaNodeRef target)
    {
        var quoted = driver.Dialect.QuoteIdentifier(target.Name);
        return driver.Caps.MultiSchema && target.Path.Count > 1
            ? $"{driver.Dialect.QuoteIdentifier(target.Path[0])}.{quoted}"
            : quoted;
    }

    private static ExportOptions Resolve(ExportOptionsDto? dto, IDbDriver driver, string fallbackName)
    {
        var options = ExportOptions.Default with { Dialect = driver.Dialect, TableName = fallbackName };
        if (dto is null) return options;

        return options with
        {
            Delimiter = dto.Delimiter ?? options.Delimiter,
            Encoding = dto.Encoding ?? options.Encoding,
            Header = dto.Header ?? options.Header,
            NullText = dto.NullText ?? options.NullText,
            DateFormat = dto.DateFormat ?? options.DateFormat,
            QuoteAll = dto.QuoteAll ?? options.QuoteAll,
            TableName = dto.TableName ?? options.TableName,
        };
    }

    private static string Sanitize(string name) =>
        new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
}
