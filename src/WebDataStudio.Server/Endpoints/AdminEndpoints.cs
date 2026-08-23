using Microsoft.Extensions.Caching.Memory;
using WebDataStudio.Server.Admin;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class AdminEndpoints
{
    public record SystemCommandRequest(string CommandId, string? Target);
    public record UserRequest(string User, string? Password, string? Privilege, string? Target);
    public record UserApplyRequest(string Hash);
    public record HashRequest(string Password);
    public record DatabaseRequest(string Name);
    public record BackupRequest(
        bool? SchemaOnly, bool? DataOnly, List<string>? Tables, string? ServerPath,
        string? Format, bool? NoOwner, bool? Clean, int? Compress);

    public static void MapAdminEndpoints(this WebApplication app)
    {
        // --- sessions --------------------------------------------------------
        // What the server is doing right now, and who is waiting for whom. One call, because the
        // overview tab asks for both every few seconds.
        // The studio's own accounts, not the database's. Secrets never leave the server, and the
        // list is read-only: accounts are deployment configuration, so a rollout is the only way to
        // change them and nobody can grant themselves a role through the UI.
        app.MapGet("/api/admin/studio-users", (UserStore users, IConfiguration config) => Results.Ok(new
        {
            anonymous = users.Anonymous,
            source = string.IsNullOrWhiteSpace(config["WDS_USERS"]) ? "WDS_USER/WDS_PASSWORD" : "WDS_USERS",
            users = users.All.Select(u => new
            {
                name = u.Name,
                role = u.Role,
                connections = u.Connections.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
                hashed = u.Secret.StartsWith("pbkdf2$", StringComparison.Ordinal),
            }),
        }));

        // Turning a password into what WDS_USERS wants. Hashing is not a secret operation - the
        // hash it returns is what goes into a deployment - but it stays behind the admin role so it
        // cannot be used as an oracle by anybody who happens to reach the studio.
        app.MapPost("/api/admin/studio-users/hash", (HashRequest body) =>
            string.IsNullOrEmpty(body.Password)
                ? Results.BadRequest(new { message = "a password is needed" })
                : Results.Ok(new { hash = UserStore.Hash(body.Password) }));

        app.MapGet("/api/admin/activity/{conn}", async (
            string conn, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.ActivityProgress)
                        return Results.Ok(new ActivityDto([], []));

                    return Results.Ok(await ServerActivity.ReadAsync(driver, session, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/admin/replication/{conn}", async (
            string conn, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.Replication)
                        return Results.Ok(Array.Empty<ReplicaState>());

                    return Results.Ok(await ServerActivity.ReplicationAsync(driver, session, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/admin/sessions/{conn}", (string conn, SessionFactory factory, CancellationToken ct) =>
            WithSession(conn, factory, ct, async (driver, session) =>
            {
                if (!driver.Caps.SessionList)
                    return Results.BadRequest(new { message = $"{driver.Info.Label} does not expose sessions" });

                return Results.Ok(await SessionService.ListAsync(driver, session, ct));
            }));

        app.MapPost("/api/admin/sessions/{conn}/{id}/kill", (string conn, string id,
            SessionFactory factory, CancellationToken ct) =>
            WithSession(conn, factory, ct, async (driver, session) =>
            {
                if (!driver.Caps.KillSession)
                    return Results.BadRequest(new
                    {
                        message = $"{driver.Info.Label} cannot terminate another session",
                    });

                await SessionService.KillAsync(driver, session, id, ct);
                return Results.NoContent();
            }));

        // --- system commands -------------------------------------------------
        app.MapGet("/api/admin/system-commands/{conn}", (string conn, SessionFactory factory,
            CancellationToken ct) =>
            WithSession(conn, factory, ct, (driver, _) =>
                Task.FromResult(Results.Ok(SystemCommandCatalog.For(driver.Info.Id)))));

        app.MapPost("/api/admin/system-command/{conn}", (string conn, SystemCommandRequest body,
            SessionFactory factory, CancellationToken ct) =>
            WithSession(conn, factory, ct, async (driver, session) =>
            {
                if (session.Spec.ReadOnly)
                    return Results.Json(new { message = "this connection is read-only" },
                        statusCode: StatusCodes.Status403Forbidden);

                var command = SystemCommandCatalog.For(driver.Info.Id)
                    .FirstOrDefault(c => c.Id.Equals(body.CommandId, StringComparison.OrdinalIgnoreCase));

                // Not in the catalogue means not runnable — this endpoint takes no raw SQL.
                if (command is null)
                    return Results.BadRequest(new { message = $"unknown command '{body.CommandId}'" });

                var sql = SystemCommandCatalog.Render(command, body.Target, driver.Dialect);

                await using var statement = session.Connection.CreateCommand();
                statement.CommandText = sql;
                statement.CommandTimeout = 0; // maintenance commands are allowed to take their time
                await statement.ExecuteNonQueryAsync(ct);

                return Results.Ok(new { executed = sql });
            }));

        // --- databases -------------------------------------------------------
        app.MapGet("/api/admin/databases/{conn}", (string conn, SessionFactory factory, CancellationToken ct) =>
            WithSession(conn, factory, ct, async (driver, session) =>
            {
                if (!driver.Caps.MultiDatabase)
                    return Results.BadRequest(new
                    {
                        message = $"{driver.Info.Label} has a single database per connection",
                    });

                return Results.Ok(await DatabaseAdmin.ListAsync(driver, session, ct));
            }));

        app.MapPost("/api/admin/databases/{conn}", (string conn, DatabaseRequest body,
            SessionFactory factory, CancellationToken ct) =>
            WithSession(conn, factory, ct, async (driver, session) =>
            {
                if (session.Spec.ReadOnly)
                    return Results.Json(new { message = "this connection is read-only" },
                        statusCode: StatusCodes.Status403Forbidden);

                await DatabaseAdmin.CreateAsync(driver, session, body.Name, ct);
                return Results.NoContent();
            }));

        app.MapDelete("/api/admin/databases/{conn}/{name}", (string conn, string name,
            SessionFactory factory, CancellationToken ct) =>
            WithSession(conn, factory, ct, async (driver, session) =>
            {
                if (session.Spec.ReadOnly)
                    return Results.Json(new { message = "this connection is read-only" },
                        statusCode: StatusCodes.Status403Forbidden);

                await DatabaseAdmin.DropAsync(driver, session, name, ct);
                return Results.NoContent();
            }));

        // --- users -----------------------------------------------------------
        app.MapGet("/api/admin/users/{conn}", (string conn, SessionFactory factory, CancellationToken ct) =>
            WithSession(conn, factory, ct, async (driver, session) =>
            {
                if (!driver.Caps.UserManagement)
                    return Results.BadRequest(new
                    {
                        message = $"{driver.Info.Label} has no user management",
                    });

                return Results.Ok(await UserAdmin.ListAsync(driver, session, ct));
            }));

        app.MapPost("/api/admin/users/{conn}/preview", (string conn, UserRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
            WithSession(conn, factory, ct, (driver, _) =>
            {
                // Every user change is previewed as SQL before it runs, same rule as everywhere else.
                var sql = body.Privilege is { Length: > 0 }
                    ? UserAdmin.GrantStatement(driver, body.User, body.Privilege, body.Target ?? "DATABASE")
                    : UserAdmin.CreateStatement(driver, body.User, body.Password ?? "");

                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
                cache.Set($"user:{hash}", sql, TimeSpan.FromMinutes(10));

                return Task.FromResult(Results.Ok(new { hash, script = sql }));
            }));

        app.MapPost("/api/admin/users/{conn}/apply", (string conn, UserApplyRequest body,
            SessionFactory factory, IMemoryCache cache, CancellationToken ct) =>
            WithSession(conn, factory, ct, async (_, session) =>
            {
                if (cache.Get($"user:{body.Hash}") is not string sql)
                    return Results.Json(new { message = "the preview expired; preview again" },
                        statusCode: StatusCodes.Status409Conflict);

                if (session.Spec.ReadOnly)
                    return Results.Json(new { message = "this connection is read-only" },
                        statusCode: StatusCodes.Status403Forbidden);

                await using var command = session.Connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(ct);

                cache.Remove($"user:{body.Hash}");
                return Results.Ok(new { executed = sql });
            }));

        // --- backup and restore ----------------------------------------------
        app.MapPost("/api/admin/backup/{conn}", async (string conn, BackupRequest body, HttpContext ctx,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (!driver.Caps.Backup)
                        return Results.BadRequest(new
                        {
                            message = $"{driver.Info.Label} has no backup support",
                        });

                    // SQLite and SQL Server back up without an external tool.
                    if (driver.Info.Id is "sqlite" or "sqlserver")
                    {
                        var path = body.ServerPath ?? Path.Combine(Path.GetTempPath(),
                            $"wds-backup-{Guid.NewGuid():n}.bak");

                        await BackupService.BackupInProcessAsync(driver, session, path, ct);

                        if (driver.Info.Id == "sqlserver")
                            return Results.Ok(new
                            {
                                serverPath = path,
                                message = "the backup was written on the database server",
                            });

                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.Headers.ContentDisposition = "attachment; filename=\"backup.sqlite\"";

                        // The copy has to be closed before it can be deleted, hence the scope.
                        await using (var file = File.OpenRead(path))
                            await file.CopyToAsync(ctx.Response.Body, ct);

                        if (body.ServerPath is null) File.Delete(path);
                        return Results.Empty;
                    }

                    var plan = BackupService.Plan(driver, session.Spec,
                        new BackupOptions(body.SchemaOnly ?? false, body.DataOnly ?? false, body.Tables,
                            body.Format, body.NoOwner ?? false, body.Clean ?? false, body.Compress));

                    if (!BackupService.ToolAvailable(plan.File))
                        return Results.BadRequest(new
                        {
                            message = $"'{plan.File}' is not installed in this container",
                        });

                    ctx.Response.ContentType = plan.ContentType;
                    ctx.Response.Headers.ContentDisposition =
                        $"attachment; filename=\"backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.{plan.Extension}\"";

                    // A tool that fails before writing anything — a version mismatch, a refused
                    // login — would otherwise arrive as an empty 200 that looks like an empty
                    // database. Count the bytes so that case can still become a real error.
                    await using var counted = new CountingStream(ctx.Response.Body);
                    var result = await ProcessRunner.RunAsync(plan.File, plan.Arguments, plan.Environment,
                        counted, ct);

                    if (result.ExitCode == 0) return Results.Empty;

                    app.Logger.LogWarning("backup tool {Tool} exited with {Code}: {Error}",
                        plan.File, result.ExitCode, result.StandardError);

                    if (counted.Written > 0)
                    {
                        // The bytes are already on their way, so the failure cannot become a status
                        // code. A plain dump can carry the reason as a comment on its last line —
                        // which is exactly where somebody restoring a truncated file will look.
                        if (plan.ContentType == "application/sql")
                            await ctx.Response.WriteAsync(
                                $"\n-- {plan.File} failed after {counted.Written} bytes: " +
                                $"{result.StandardError.Trim().ReplaceLineEndings(" ")}\n", ct);

                        return Results.Empty;
                    }

                    return Results.Json(new
                    {
                        message = $"{plan.File} failed: {result.StandardError.Trim()}",
                    }, statusCode: 502);
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapPost("/api/admin/restore/{conn}", async (string conn, HttpRequest request,
            SessionFactory factory, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "expected a file upload" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            var confirmation = form["confirm"].ToString();

            if (file is null) return Results.BadRequest(new { message = "no file was uploaded" });

            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    if (session.Spec.ReadOnly)
                        return Results.Json(new { message = "this connection is read-only" },
                            statusCode: StatusCodes.Status403Forbidden);

                    // Restore overwrites a database. The caller has to name it back to us.
                    var database = session.Connection.Database;
                    if (!string.Equals(confirmation, database, StringComparison.Ordinal))
                        return Results.BadRequest(new
                        {
                            message = $"type the target database name ({database}) to confirm the restore",
                        });

                    var plan = BackupService.RestorePlan(driver, session.Spec);
                    if (!BackupService.ToolAvailable(plan.File))
                        return Results.BadRequest(new
                        {
                            message = $"'{plan.File}' is not installed in this container",
                        });

                    await using var upload = file.OpenReadStream();
                    var result = await ProcessRunner.RunAsync(plan.File, plan.Arguments, plan.Environment,
                        upload, feedStdin: true, ct);

                    return result.ExitCode == 0
                        ? Results.Ok(new { message = "restore finished" })
                        : Results.BadRequest(new { message = result.StandardError });
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        }).DisableAntiforgery();

        // --- server logs ------------------------------------------------------
        app.MapGet("/api/admin/logs/{conn}", (string conn, int? lines, SessionFactory factory,
            CancellationToken ct) =>
            WithSession(conn, factory, ct, async (driver, session) =>
            {
                var sql = driver.Info.Id switch
                {
                    "mysql" => "SELECT logged, prio, error_code, data FROM performance_schema.error_log " +
                               $"ORDER BY logged DESC LIMIT {Math.Clamp(lines ?? 200, 1, 2000)}",
                    "sqlserver" => "EXEC sp_readerrorlog",
                    "postgresql" => "SELECT pg_read_file(pg_current_logfile(), 0, 100000)",
                    _ => null,
                };

                // Three distinct answers: cannot, may not, and here they are.
                if (sql is null)
                    return Results.Ok(new
                    {
                        available = false,
                        reason = $"{driver.Info.Label} does not expose its log through SQL",
                        lines = Array.Empty<string>(),
                    });

                try
                {
                    var entries = new List<string>();

                    await using var command = session.Connection.CreateCommand();
                    command.CommandText = sql;

                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        entries.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount)
                            .Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString())));

                    return Results.Ok(new { available = true, reason = (string?)null, lines = entries });
                }
                catch (Exception e)
                {
                    return Results.Ok(new
                    {
                        available = false,
                        reason = $"this role cannot read the server log: {e.Message}",
                        lines = Array.Empty<string>(),
                    });
                }
            }));
    }

    private static async Task<IResult> WithSession(string conn, SessionFactory factory, CancellationToken ct,
        Func<IDbDriver, IDbSession, Task<IResult>> action)
    {
        try
        {
            var (driver, session) = await factory.OpenAsync(conn, ct);
            await using (session) return await action(driver, session);
        }
        catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
        catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (InvalidOperationException e) { return Results.BadRequest(new { message = e.Message }); }
        catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
    }
}
