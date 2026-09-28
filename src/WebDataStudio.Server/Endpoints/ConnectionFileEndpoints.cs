using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// A file as a connection.
///
/// The form has had a "File path" field for SQLite and DuckDB since the beginning, and it takes a
/// path *on the server* — right for a mounted share, useless for the file on the laptop in front of
/// you, because the container cannot see it. So: upload it, or pick it where the server can.
public static class ConnectionFileEndpoints
{
    /// One folder per connection, named by its id: two uploads called `shop.db` are two databases,
    /// and the second must not land on the first.
    ///
    /// A file a session owns lives one level deeper, under a folder named after the session, so that
    /// everything one visitor brought can be swept in one move when they are gone.
    public static string UploadDirectoryFor(FileRoots roots, string connectionId,
        string? sessionKey = null) =>
        sessionKey is { Length: > 0 } key
            ? Path.Combine(roots.Uploads, "session", SessionConnections.FolderFor(key), connectionId)
            : Path.Combine(roots.Uploads, connectionId);

    public static void MapConnectionFileEndpoints(this WebApplication app)
    {
        app.MapPost("/api/connections/file", async (HttpRequest request, FileRoots roots,
            ConnectionStore store, SessionConnections sessions, StudioAccess access,
            CancellationToken ct) =>
        {
            // Before the body is read: a closed door should not first take a database off somebody.
            if (!access.MayUpload)
                return Results.Json(new
                {
                    message = "this studio does not take uploaded database files; a deployment "
                              + "allows it with WDS_ALLOW_FILE_UPLOAD",
                }, statusCode: StatusCodes.Status403Forbidden);

            if (!request.HasFormContentType)
                return Results.BadRequest(new { message = "send the file as multipart/form-data" });

            // A multipart body with nothing in it makes ReadFormAsync throw, and a throw here is a
            // 500 about a request that is merely empty.
            IFormCollection form;

            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { message = "no file was sent" });
            }

            var file = form.Files["file"];

            if (file is null || file.Length == 0)
                return Results.BadRequest(new { message = "no file was sent" });

            // Checked before the copy: half a database on the disk of a studio anybody can reach is
            // what this setting exists to prevent.
            if (file.Length > access.UploadMaxBytes)
                return Results.Json(new
                {
                    message = $"this file is larger than the {access.UploadMaxBytes / (1024 * 1024)} MB "
                              + "this studio takes (WDS_UPLOAD_MAX_MB)",
                }, statusCode: StatusCodes.Status413PayloadTooLarge);

            var fileName = Path.GetFileName(file.FileName);

            if (FileConnections.Refusal(fileName) is { } refusal)
                return Results.BadRequest(new { message = refusal });

            var kind = FileConnections.KindOf(fileName);

            if (kind == FileConnectionKind.Unsupported)
                return Results.BadRequest(new
                {
                    message = $"'{Path.GetExtension(fileName)}' is not a database this studio opens — "
                              + "SQLite, DuckDB, Parquet, CSV and NDJSON are",
                });

            // The id is decided here so it can name the folder; whoever keeps the connection
            // keeps the id.
            var id = Guid.NewGuid().ToString("n");
            var session = access.Scope == ConnectionScope.Session
                ? SessionConnections.Key(request.HttpContext)
                : null;
            var directory = UploadDirectoryFor(roots, id, session);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);

            await using (var target = File.Create(path))
                await file.CopyToAsync(target, ct);

            // A `.db` is whatever somebody renamed. Checked after the copy, because the check reads
            // the file, and refused without keeping it.
            if (kind == FileConnectionKind.Sqlite && !FileConnections.LooksLikeSqlite(path))
            {
                Directory.Delete(directory, true);

                return Results.BadRequest(new
                {
                    message = "this file is not a SQLite database — it does not start with "
                              + "'SQLite format 3'",
                });
            }

            var name = form["name"].ToString() is { Length: > 0 } given ? given : fileName;

            // A studio where connections belong to one browser: nothing is written down, so there is
            // no shared name to collide with and nothing for the loop below to work around.
            if (session is not null)
                return Results.Ok(ConnectionRegistry.ToDto(sessions.Add(session,
                    new ConnectionSpec(id, name, FileConnections.EngineOf(kind),
                        FileConnections.ConnectionStringFor(kind, path),
                        FileConnections.ReadOnlyByNature(kind), null, null,
                        ConnectionSource.Session))));

            // Uploading yesterday's export and today's is uploading two files with one name, which
            // the store refuses — rightly, a name is how a connection is addressed. So the second
            // one becomes "shop.db (2)" rather than an error about something the person did not do.
            for (var attempt = 1; ; attempt++)
            {
                var candidate = attempt == 1 ? name : $"{name} ({attempt})";

                try
                {
                    var spec = store.Add(new ConnectionSpec(id, candidate, FileConnections.EngineOf(kind),
                        FileConnections.ConnectionStringFor(kind, path),
                        FileConnections.ReadOnlyByNature(kind), null, null, ConnectionSource.Stored));

                    return Results.Ok(ConnectionRegistry.ToDto(spec));
                }
                catch (InvalidOperationException e) when (attempt < 20)
                {
                    _ = e;
                }
                catch (InvalidOperationException e)
                {
                    Directory.Delete(directory, true);
                    return Results.Conflict(new { message = e.Message });
                }
            }
        }).DisableAntiforgery();

        app.MapGet("/api/connections/browse", (string? path, FileRoots roots, StudioAccess access) =>
        {
            // Closed for the listing and for a path somebody guessed: it is the same door.
            if (!access.MayBrowse)
                return Results.Json(new
                {
                    message = "this studio does not let its users browse the server's folders; a "
                              + "deployment allows it with WDS_ALLOW_FILE_BROWSE",
                }, statusCode: StatusCodes.Status403Forbidden);

            // No path yet: the roots are where a picker starts.
            if (string.IsNullOrWhiteSpace(path))
                return Results.Ok(new
                {
                    path = (string?)null,
                    roots = roots.All,
                    parent = (string?)null,
                    directories = Array.Empty<object>(),
                    files = Array.Empty<object>(),
                });

            if (roots.Resolve(path) is not { } resolved || !Directory.Exists(resolved))
                return Results.Json(new
                {
                    message = "this folder is not one this studio may read — a deployment names the "
                              + "folders it may in WDS_FILE_ROOTS",
                }, statusCode: StatusCodes.Status403Forbidden);

            var parent = Path.GetDirectoryName(resolved) is { Length: > 0 } up ? roots.Resolve(up) : null;

            return Results.Ok(new
            {
                path = resolved,
                roots = roots.All,
                parent,
                directories = Directory.EnumerateDirectories(resolved)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { name = Path.GetFileName(d), path = d })
                    .ToList(),
                // A file nothing opens is listed with no engine rather than hidden: seeing it and
                // being told nothing reads it beats wondering where the file went.
                files = Directory.EnumerateFiles(resolved)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new
                    {
                        name = Path.GetFileName(f),
                        path = f,
                        size = new FileInfo(f).Length,
                        engine = FileConnections.KindOf(f) is var kind
                                 && kind != FileConnectionKind.Unsupported
                            ? FileConnections.EngineOf(kind)
                            : null,
                    })
                    .ToList(),
            });
        });

        // A folder for a bucket that is a folder: picked where the server can see it, and made
        // there when it is not there yet. Writing to the server's disk is an admin's call, and it
        // stays inside the same roots the listing is fenced to.
        app.MapPost("/api/connections/browse/folder",
            (NewFolderRequest body, FileRoots roots, StudioAccess access, CurrentUser current) =>
        {
            if (!access.MayBrowse || !access.MayAdd)
                return Results.Json(new { message = "this studio does not let its users browse the server's folders" },
                    statusCode: StatusCodes.Status403Forbidden);

            // Null is a studio without accounts, where whoever is here runs it.
            if (current.User is { IsAdmin: false })
                return Results.Json(new { message = "making a folder on the server needs the admin role" },
                    statusCode: StatusCodes.Status403Forbidden);

            var name = body.Name?.Trim() ?? "";
            if (name is "" or "." or ".." || name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) >= 0)
                return Results.BadRequest(new { message = "a folder name is one name, without slashes" });

            if (roots.Resolve(body.Path ?? "") is not { } parent || !Directory.Exists(parent)
                || roots.Resolve(Path.Combine(parent, name)) is not { } target)
                return Results.Json(new { message = "this folder is not one this studio may write to" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (Directory.Exists(target) || File.Exists(target))
                return Results.Conflict(new { message = $"{name} is already there" });

            Directory.CreateDirectory(target);
            return Results.Ok(new { path = target });
        });
    }

    public record NewFolderRequest(string? Path, string? Name);
}
