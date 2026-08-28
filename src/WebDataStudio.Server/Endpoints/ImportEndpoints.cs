using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.Storage;
using WebDataStudio.Server.Import;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class ImportEndpoints
{
    /// A bare name: the schema travels on its own, so nothing here is spliced into an identifier.
    private static string? TableName(string table)
    {
        var trimmed = table.Trim();

        return trimmed.Length == 0
               || trimmed.Contains('.') || trimmed.Contains('"') || trimmed.Contains('`')
               || trimmed.Contains('[') || trimmed.Contains(';')
            ? null
            : trimmed;
    }

    /// Where a new table goes when nobody said: the engine's usual default, and nothing for the
    /// engines that have no schemas at all.
    private static string DefaultSchema(IDbDriver driver) => driver.Info.Id switch
    {
        "postgresql" or "duckdb" => "public",
        "sqlserver" => "dbo",
        _ => "",
    };

    public record CopyTableRequest(string SourceConnectionId, string SourceRef,
        string TargetConnectionId, string TargetTable, int? MaxRows);

    private const long MaxUploadBytes = 512L * 1024 * 1024;

    public static void MapImportEndpoints(this WebApplication app)
    {
        // --- a file becomes a table ------------------------------------------
        // The import below fills a table that already exists. This is the other half: a CSV or a
        // Parquet somebody was sent, or an object in a bucket, that should simply be a table. DuckDB
        // reads it — it infers a CSV's types better than a hand-rolled sniffer and takes an s3:// URI
        // as readily as a path — and the target engine's own DDL writer creates the table.
        app.MapPost("/api/import/{conn}/new-table", async (string conn, string table, string? schema,
            bool? apply, string? storageConnection, [FromQuery(Name = "ref")] string? objectRef,
            HttpRequest request, SessionFactory factory, FileTableImport imports,
            CancellationToken ct) =>
        {
            if (TableName(table) is not { } target)
                return Results.BadRequest(new
                {
                    message = "a table name cannot be qualified or quoted here; "
                              + "pass the schema on its own",
                });

            string? staged = null;

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    FileSource source;

                    if (storageConnection is { Length: > 0 } && objectRef is { Length: > 0 })
                    {
                        // An object in a bucket is read where it is: no download, no temp file.
                        var (_, storageSession) = await factory.OpenAsync(storageConnection, ct);
                        await using (storageSession)
                        {
                            if (storageSession.Unwrap() is not StorageSession storage)
                                return Results.BadRequest(new
                                {
                                    message = $"'{storageConnection}' is not object storage",
                                });

                            var objectTarget = SchemaEndpoints.ParseObjectRef(objectRef);
                            var key = StorageDriver.KeyOf(objectTarget);
                            var setup = new List<string>(DuckDbExtensions.Preamble(
                                storage.Store.Target.Provider, DuckDbExtensions.BundledDirectory));

                            if (storage.Store.SecretStatement() is { } secret) setup.Add(secret);

                            source = new StorageFileSource(storage.Store.SqlUri(key), setup);
                        }
                    }
                    else
                    {
                        if (!request.HasFormContentType)
                            return Results.BadRequest(new
                            {
                                message = "upload a file, or name a storage connection and an object",
                            });

                        var form = await request.ReadFormAsync(ct);
                        var file = form.Files.GetFile("file");

                        if (file is null)
                            return Results.BadRequest(new { message = "no file was uploaded" });

                        if (file.Length > MaxUploadBytes)
                            return Results.BadRequest(new
                            {
                                message = "the file is larger than the 512 MB upload limit",
                            });

                        // DuckDB reads a file rather than a stream, so an upload is staged here and
                        // deleted again below whatever happens.
                        staged = Path.Combine(Path.GetTempPath(),
                            "wds-import-" + Guid.NewGuid().ToString("N")
                            + Path.GetExtension(file.FileName));

                        await using (var write = File.Create(staged))
                            await file.CopyToAsync(write, ct);

                        source = new LocalFileSource(staged);
                    }

                    var schemaName = schema ?? DefaultSchema(driver);

                    // Two calls, one endpoint: the plan is read before anything is created, which is
                    // the handshake every other change in this studio uses.
                    if (apply != true)
                        return Results.Ok(
                            await imports.PlanAsync(driver, schemaName, target, source, ct));

                    return Results.Ok(
                        await imports.RunAsync(driver, session, schemaName, target, source, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
            finally
            {
                if (staged is not null && File.Exists(staged))
                    try { File.Delete(staged); } catch (IOException) { }
            }
        }).DisableAntiforgery();

        app.MapPost("/api/import/preview", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "expected a file upload" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { message = "no file was uploaded" });
            if (file.Length > MaxUploadBytes)
                return Results.BadRequest(new { message = "the file is larger than the 512 MB upload limit" });

            var format = form["format"].ToString() is { Length: > 0 } explicitFormat
                ? explicitFormat
                : ImportSources.DetectFormat(file.FileName);

            if (format is null)
                return Results.BadRequest(new { message = $"cannot tell the format of '{file.FileName}'" });

            try
            {
                var settings = ReadSettings(form);
                await using var stream = file.OpenReadStream();
                var preview = await ImportSources.Get(format).PreviewAsync(stream, settings, ct);

                return Results.Ok(new
                {
                    format,
                    preview.Columns,
                    preview.SampleRows,
                    preview.DetectedTypes,
                    suggestedMapping = preview.Columns.ToDictionary(c => c, c => c),
                });
            }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.BadRequest(new { message = $"could not read the file: {e.Message}" }); }
        }).DisableAntiforgery();

        app.MapPost("/api/import/execute", async (HttpRequest request, SessionFactory factory,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "expected a file upload" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { message = "no file was uploaded" });

            var connectionId = form["connectionId"].ToString();
            var table = form["table"].ToString();
            if (connectionId.Length == 0 || table.Length == 0)
                return Results.BadRequest(new { message = "connectionId and table are required" });

            var format = form["format"].ToString() is { Length: > 0 } explicitFormat
                ? explicitFormat
                : ImportSources.DetectFormat(file.FileName);
            if (format is null)
                return Results.BadRequest(new { message = $"cannot tell the format of '{file.FileName}'" });

            // mapping maps source column -> target column; an unmapped source column is skipped.
            var mapping = ParseMapping(form["mapping"].ToString());

            try
            {
                var (driver, session) = await factory.OpenAsync(connectionId, ct);
                await using (session)
                {
                    var settings = ReadSettings(form);
                    var source = ImportSources.Get(format);

                    await using var stream = file.OpenReadStream();

                    if (format == "sql")
                    {
                        var executed = await RunScriptAsync(driver, session, source, stream, settings, ct);
                        return Results.Ok(new { inserted = executed, failed = 0, errors = Array.Empty<string>() });
                    }

                    var preview = await PreviewFromCopyAsync(source, file, settings, ct);
                    var (targets, indexes) = ResolveMapping(preview.Columns, mapping);
                    if (targets.Count == 0)
                        return Results.BadRequest(new { message = "no columns were mapped to the target table" });

                    await using var rowStream = file.OpenReadStream();
                    var rows = Project(source.ReadAsync(rowStream, settings, ct), indexes);

                    var result = await new ImportService().ExecuteAsync(driver, session, table, targets, rows, ct);
                    return Results.Ok(new { result.Inserted, result.Failed, result.Errors });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (InvalidOperationException e)
            {
                // Read-only connections and unmapped columns land here: the caller's mistake, not ours.
                return Results.Json(new { message = e.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        }).DisableAntiforgery();

        app.MapPost("/api/copy-table", async (CopyTableRequest body, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var result = await new TableCopyService(factory).CopyAsync(
                    body.SourceConnectionId, body.SourceRef, body.TargetConnectionId, body.TargetTable,
                    body.MaxRows ?? int.MaxValue, ct);

                return Results.Ok(new { result.Inserted, result.Failed, result.Errors });
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (InvalidOperationException e)
            {
                return Results.Json(new { message = e.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    private static ImportSettings ReadSettings(IFormCollection form) => new(
        HasHeader: form["hasHeader"].ToString() is not "false",
        Delimiter: form["delimiter"].ToString() is { Length: > 0 } d ? d : ",",
        Encoding: form["encoding"].ToString() is { Length: > 0 } e ? e : "utf-8",
        SheetName: form["sheet"].ToString() is { Length: > 0 } s ? s : null);

    private static Dictionary<string, string> ParseMapping(string json)
    {
        if (json.Length == 0) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// Turns the source-to-target mapping into the target column list plus the source indexes to
    /// read, so the row projection below never has to look at names again.
    private static (List<string> Targets, List<int> Indexes) ResolveMapping(
        IReadOnlyList<string> sourceColumns, Dictionary<string, string> mapping)
    {
        var targets = new List<string>();
        var indexes = new List<int>();

        for (var i = 0; i < sourceColumns.Count; i++)
        {
            // An empty mapping means "same names"; an explicit empty value means "skip".
            var target = mapping.Count == 0
                ? sourceColumns[i]
                : mapping.GetValueOrDefault(sourceColumns[i], "");

            if (target.Length == 0) continue;
            targets.Add(target);
            indexes.Add(i);
        }

        return (targets, indexes);
    }

    private static async IAsyncEnumerable<object?[]> Project(IAsyncEnumerable<object?[]> rows,
        IReadOnlyList<int> indexes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in rows.WithCancellation(ct))
            yield return indexes.Select(i => i < row.Length ? row[i] : null).ToArray();
    }

    private static async Task<ImportPreview> PreviewFromCopyAsync(IImportSource source,
        IFormFile file, ImportSettings settings, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return await source.PreviewAsync(stream, settings, ct);
    }

    private static async Task<int> RunScriptAsync(Drivers.Abstractions.IDbDriver driver,
        Drivers.Abstractions.IDbSession session, IImportSource source, Stream stream,
        ImportSettings settings, CancellationToken ct)
    {
        var executed = 0;

        await foreach (var row in source.ReadAsync(stream, settings, ct))
        {
            var script = row[0]?.ToString() ?? "";
            var request = new Drivers.Abstractions.ScriptRequest(script, MaxRows: 0, TimeoutSeconds: 3600);

            await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
            {
                if (chunk is Drivers.Abstractions.ResultChunk.Error error)
                    throw new InvalidOperationException(error.Text);
                if (chunk is Drivers.Abstractions.ResultChunk.End) executed++;
            }
        }

        return executed;
    }
}
