using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.Storage;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;
using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Endpoints;

/// The object itself, rather than the rows inside it: a bounded preview, a download, an upload and a
/// delete.
///
/// Reading is free of ceremony; changing is not. A read-only connection refuses to write, and so does
/// one marked as production — the same rule the exporter already applies, for the same reason.
public static class StorageEndpoints
{
    public static void MapStorageEndpoints(this WebApplication app)
    {
        // A preview reads the front of a file and never the whole thing: a 4 GB Parquet must not
        // become a 4 GB response because somebody clicked on it.
        var previewBytes = int.TryParse(app.Configuration["WDS_STORAGE_PREVIEW_BYTES"], out var p)
            ? p : 64 * 1024;

        var maxUpload = long.TryParse(app.Configuration["WDS_STORAGE_MAX_UPLOAD_BYTES"], out var u)
            ? u : 64L * 1024 * 1024;

        var archiveLimits = ArchiveLimits.FromConfiguration(app.Configuration);

        app.MapGet("/api/storage/{conn}/preview", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (StoreOf(session) is not { } store)
                        return Results.BadRequest(new { message = NotStorage });

                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var key = StorageDriver.KeyOf(target);
                    var head = await store.HeadAsync(key, ct);
                    if (head is null) return Results.NotFound(new { message = $"no object '{key}'" });

                    await using var stream = await store.OpenReadAsync(key, ct);
                    var buffer = new byte[previewBytes + 1];
                    var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, ct);

                    var truncated = read > previewBytes;
                    var content = buffer.AsSpan(0, Math.Min(read, previewBytes)).ToArray();
                    var text = LooksTextual(head.ContentType, content);

                    return Results.Ok(new
                    {
                        name = target.Name,
                        key,
                        contentType = head.ContentType,
                        size = head.SizeBytes,
                        modified = head.Modified,
                        etag = head.ETag,
                        storageClass = head.StorageClass,
                        // A file that can be read as a table is offered as one; the preview is what
                        // is left for everything else.
                        queryable = StorageReader.CanRead(key),
                        // What a query would select from, and the URI to copy — both come from the
                        // driver so the browser never has to know one provider's spelling from
                        // another's.
                        from = driver.FromClause(session, target),
                        uri = store.SqlUri(key),
                        truncated,
                        text = text ? System.Text.Encoding.UTF8.GetString(content) : null,
                        binary = !text,
                    });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            // A container that was never created: one sentence, rather than the provider's
            // page of XML about it.
            catch (StorageContainerMissingException e) { return Results.NotFound(new { message = e.Message }); }
            catch (StorageUnreachableException e)
            {
                // Not this studio's fault and not a bad request: something it depends on
                // stopped answering, which is what 504 is for.
                return Results.Json(new { message = e.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // `inline=true` is the same bytes without the attachment header: a PDF, an audio file or a
        // video the browser can show where it is. Everything else is a download, because a file the
        // browser cannot show and does not save is a blank tab.
        app.MapGet("/api/storage/{conn}/download", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, bool? inline, SessionFactory factory,
            CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                var store = StoreOf(session);
                if (store is null)
                {
                    await session.DisposeAsync();
                    return Results.BadRequest(new { message = NotStorage });
                }

                var target = SchemaEndpoints.ParseObjectRef(objectRef);
                var key = StorageDriver.KeyOf(target);
                var head = await store.HeadAsync(key, ct);

                if (head is null)
                {
                    await session.DisposeAsync();
                    return Results.NotFound(new { message = $"no object '{key}'" });
                }

                // The body outlives this handler: the bytes are still being read from the bucket
                // long after it returns. So the session travels with the stream and is handed back
                // when ASP.NET closes it — which it does whether the response finished or the
                // client went away. Without that, every file downloaded costs the connection one
                // of its session slots, permanently, and the fourth one makes it hang.
                var stream = new SessionOwningStream(await store.OpenReadAsync(key, ct), session);

                // A range-enabled response is what a video player needs to seek, and it needs a
                // seekable stream to answer one — which is why only the shown-in-place case asks for
                // it: a provider's read stream is a network stream, and a download does not seek.
                return inline == true
                    ? Results.Stream(stream, head.ContentType ?? "application/octet-stream",
                        lastModified: head.Modified, entityTag: null, enableRangeProcessing: false)
                    : Results.Stream(stream, head.ContentType ?? "application/octet-stream",
                        fileDownloadName: target.Name, enableRangeProcessing: false);
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            // A container that was never created: one sentence, rather than the provider's
            // page of XML about it.
            catch (StorageContainerMissingException e) { return Results.NotFound(new { message = e.Message }); }
            catch (StorageUnreachableException e)
            {
                // Not this studio's fault and not a bad request: something it depends on
                // stopped answering, which is what 504 is for.
                return Results.Json(new { message = e.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // A whole folder, as one zip. "Save this file" was a download; "give me this folder" was a
        // click per file.
        app.MapGet("/api/storage/{conn}/archive", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, HttpContext ctx, SessionFactory factory,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                var store = StoreOf(session);

                if (store is null)
                {
                    await session.DisposeAsync();
                    return Results.BadRequest(new { message = NotStorage });
                }

                var target = SchemaEndpoints.ParseObjectRef(objectRef);
                var prefix = StorageDriver.KeyOf(target);
                var name = (target.Name.Length > 0 ? target.Name : "archive") + ".zip";

                Audit.Detail(ctx, $"zip of {prefix}", conn);

                // ZipArchive has no asynchronous write path — CreateEntry().Open() is a synchronous
                // stream — and Kestrel refuses synchronous writes unless a response asks for them.
                // Asked for here and nowhere else: the alternative is buffering a whole folder in
                // memory to have something to write asynchronously, which is worse in every way.
                if (ctx.Features.Get<IHttpBodyControlFeature>() is { } control)
                    control.AllowSynchronousIO = true;

                // A zip has no length until it is written, so this is a streamed response: the
                // session goes back to the pool when the body has been written.
                return Results.Stream(async stream =>
                {
                    try
                    {
                        var result = await StorageArchive.WriteAsync(store, prefix, stream,
                            archiveLimits, ct);

                        logs.CreateLogger("WebDataStudio.Storage").LogInformation(
                            "zipped {Objects} object(s), {Bytes} byte(s) from {Prefix}{Stopped}",
                            result.Objects, result.Bytes, prefix,
                            result.Stopped is null ? "" : $" — {result.Stopped}");
                    }
                    finally
                    {
                        await session.DisposeAsync();
                    }
                }, "application/zip", fileDownloadName: name);
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            // A container that was never created: one sentence, rather than the provider's
            // page of XML about it.
            catch (StorageContainerMissingException e) { return Results.NotFound(new { message = e.Message }); }
            catch (StorageUnreachableException e)
            {
                // Not this studio's fault and not a bad request: something it depends on
                // stopped answering, which is what 504 is for.
                return Results.Json(new { message = e.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/storage/{conn}/upload", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, string name, HttpRequest request,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (StoreOf(session) is not { } store)
                        return Results.BadRequest(new { message = NotStorage });

                    if (Refusal(session.Spec) is { } refusal)
                        return Results.Json(new { message = refusal }, statusCode: 403);

                    if (FileName(name) is not { } fileName)
                        return Results.BadRequest(new
                        {
                            message = "a file name cannot contain a path; upload into the folder " +
                                      "that is open instead",
                        });

                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    var prefix = StorageDriver.KeyOf(target);
                    var key = prefix.Length == 0 ? fileName : $"{prefix.TrimEnd('/')}/{fileName}";

                    // The S3 SDK signs a payload it can rewind, and an HTTP request body cannot be
                    // rewound, so the content is buffered — with a ceiling, because a request that
                    // does not fit in memory is one this endpoint should refuse rather than absorb.
                    var buffer = new MemoryStream();
                    var copied = await CopyBoundedAsync(request.Body, buffer, maxUpload, ct);
                    if (copied is null)
                        return Results.Json(new
                        {
                            message = $"the upload is larger than {maxUpload} bytes; " +
                                      "raise WDS_STORAGE_MAX_UPLOAD_BYTES or use the provider's own tool",
                        }, statusCode: 413);

                    buffer.Position = 0;
                    await store.WriteAsync(key, buffer, request.ContentType, ct);

                    return Results.Ok(new { key, bytes = copied });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            // A container that was never created: one sentence, rather than the provider's
            // page of XML about it.
            catch (StorageContainerMissingException e) { return Results.NotFound(new { message = e.Message }); }
            catch (StorageUnreachableException e)
            {
                // Not this studio's fault and not a bad request: something it depends on
                // stopped answering, which is what 504 is for.
                return Results.Json(new { message = e.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapDelete("/api/storage/{conn}", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (_, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (StoreOf(session) is not { } store)
                        return Results.BadRequest(new { message = NotStorage });

                    if (Refusal(session.Spec) is { } refusal)
                        return Results.Json(new { message = refusal }, statusCode: 403);

                    var target = SchemaEndpoints.ParseObjectRef(objectRef);
                    if (target.Kind != SchemaNodeKind.StorageObject)
                        return Results.BadRequest(new
                        {
                            // Deleting a prefix means deleting everything under it, which is not a
                            // click this studio offers.
                            message = "only a single object can be deleted here",
                        });

                    var key = StorageDriver.KeyOf(target);
                    await store.DeleteAsync(key, ct);

                    return Results.Ok(new { key, deleted = true });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            // A container that was never created: one sentence, rather than the provider's
            // page of XML about it.
            catch (StorageContainerMissingException e) { return Results.NotFound(new { message = e.Message }); }
            catch (StorageUnreachableException e)
            {
                // Not this studio's fault and not a bad request: something it depends on
                // stopped answering, which is what 504 is for.
                return Results.Json(new { message = e.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    private const string NotStorage = "this connection is not object storage";

    /// Why a write is refused, or null if it is allowed.
    private static string? Refusal(ConnectionSpec spec)
    {
        if (spec.ReadOnly) return "this connection is read-only; nothing was changed";

        return string.Equals(spec.Color, "red", StringComparison.OrdinalIgnoreCase)
            ? "this connection is marked as production; uploads and deletes are refused. " +
              "Remove the production colour if that is really intended."
            : null;
    }

    private static IObjectStore? StoreOf(IDbSession session) =>
        session.Unwrap() is StorageSession storage ? storage.Store : null;

    /// The name with nothing in it that could point somewhere else, or null if it tried to.
    private static string? FileName(string name)
    {
        var trimmed = name.Trim();

        return trimmed.Length == 0 || trimmed.Contains('/') || trimmed.Contains('\\')
               || trimmed.Contains("..")
            ? null
            : trimmed;
    }

    /// Whether this is worth showing as text. The content type is taken at its word where it says
    /// something useful, and where it does not — `application/octet-stream` is what most uploads
    /// arrive as — the bytes decide.
    private static bool LooksTextual(string? contentType, ReadOnlySpan<byte> content)
    {
        if (contentType is { Length: > 0 })
        {
            if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var textual in new[] { "json", "csv", "xml", "yaml", "javascript", "sql" })
                if (contentType.Contains(textual, StringComparison.OrdinalIgnoreCase)) return true;

            // Shown or downloaded, never read as text: a PDF is mostly ASCII near the front, and
            // the NUL test below let its object headers through as a "preview" nobody wants.
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("zip", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("parquet", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // A NUL byte near the front is the oldest and still the best test for "not text".
        var head = content.Length > 512 ? content[..512] : content;
        return head.IndexOf((byte)0) < 0;
    }

    /// Copies at most `max` bytes, or null when there were more than that.
    private static async Task<long?> CopyBoundedAsync(
        Stream from, Stream to, long max, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > max) return null;

            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return total;
    }
}
