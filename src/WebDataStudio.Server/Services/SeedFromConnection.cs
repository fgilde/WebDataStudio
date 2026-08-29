using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Import;

namespace WebDataStudio.Server.Services;

/// One "fill this database from that one", as the file describes it.
public sealed record SeedCopy(
    string From, string To, IReadOnlyList<string> Tables, int? MaxRows, string? Schema);

/// Where the copies are described, if anywhere. Off without a file, like every other thing the
/// studio would otherwise do to a database without being asked.
public sealed record SeedFromOptions(bool Configured, string File)
{
    public static SeedFromOptions FromConfiguration(IConfiguration config)
    {
        var file = config["WDS_SEED_FROM_FILE"]?.Trim();

        return string.IsNullOrEmpty(file) ? new SeedFromOptions(false, "") : new SeedFromOptions(true, file);
    }
}

/// Fills an empty development database from another connection, once, at start.
///
/// A seed script is the answer when you can write the data down. This is the answer when you
/// cannot: the shape and the rows already exist somewhere — a staging server, a container the stack
/// brought up with a sample database in it — and a fresh stack should come up with them rather than
/// with empty tables.
///
/// The rules are the seed script's rules, plus one: **a table that already exists is left alone**.
/// A restart is not a reason to overwrite what somebody has been working on for an hour.
public sealed class SeedFromConnection(
    SeedFromOptions options, ConnectionRegistry registry, SessionFactory factory,
    ILogger<SeedFromConnection> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Configured => options.Configured;

    public IReadOnlyList<SeedCopy> Read()
    {
        if (!options.Configured || !File.Exists(options.File)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<SeedCopy>>(File.ReadAllText(options.File), Json) ?? [];
        }
        catch (Exception e)
        {
            log.LogWarning(e, "the seed-from file at {File} could not be read", options.File);
            return [];
        }
    }

    /// Returns how many tables were actually copied.
    public async Task<int> RunAsync(CancellationToken ct) => (await SweepAsync(ct)).Seeded;

    /// The same, and how many tables could not be copied yet — a source that is still starting,
    /// which is the normal state of a database thirty seconds into a stack coming up.
    public async Task<SeedSweep> SweepAsync(CancellationToken ct)
    {
        if (!options.Configured) return new SeedSweep(0, 0);

        var copied = 0;
        var pending = 0;

        foreach (var copy in Read())
        {
            if (copy.From is not { Length: > 0 } || copy.To is not { Length: > 0 }) continue;

            var target = registry.Find(copy.To);

            if (target is null)
            {
                // The target may simply not be registered yet; worth another look.
                log.LogWarning("there is no connection called {Connection} to seed", copy.To);
                pending += copy.Tables?.Count ?? 0;
                continue;
            }

            if (target.ReadOnly)
            {
                log.LogInformation("{Connection} is read-only, so nothing was copied into it", copy.To);
                continue;
            }

            // Red is the studio's convention for production, and the seed script honours it for the
            // same reason: filling a production database from somewhere else is the worst kind of
            // helpful.
            if (string.Equals(target.Color, "red", StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("{Connection} is marked as production, so nothing was copied into it",
                    copy.To);
                continue;
            }

            foreach (var table in copy.Tables ?? [])
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (await ExistsAsync(target.Id, table, ct))
                    {
                        log.LogInformation("{Table} already exists in {Connection} and was left alone",
                            table, copy.To);
                        continue;
                    }

                    var outcome = await new ResultTableImport(factory).RunAsync(
                        copy.From, $"SELECT * FROM {table}", target.Id, copy.Schema ?? "", table,
                        copy.MaxRows ?? 10_000, ct);

                    log.LogInformation("copied {Rows} row(s) of {Table} from {From} into {To}",
                        outcome.Rows, table, copy.From, copy.To);

                    copied++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    // One table that will not copy is not a reason for the others not to — and the
                    // reason is often "not up yet", so it is worth coming back for.
                    log.LogWarning(e, "{Table} could not be copied from {From} into {To}",
                        table, copy.From, copy.To);
                    pending++;
                }
            }
        }

        return new SeedSweep(copied, pending);
    }

    /// Whether the target already has this table. Asked by describing it: every driver can, and a
    /// failure to describe is exactly what "it is not there" looks like.
    private async Task<bool> ExistsAsync(string connectionId, string table, CancellationToken ct)
    {
        var (driver, session) = await factory.OpenAsync(connectionId, ct);
        await using (session)
        {
            foreach (var root in await driver.IntrospectAsync(session, null, ct))
            {
                foreach (var child in await driver.IntrospectAsync(session, root.Ref, ct))
                {
                    if (child.Ref.Kind is SchemaNodeKind.Table
                        && child.Ref.Name.Equals(table, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (child.Ref.Kind is not (SchemaNodeKind.TableFolder or SchemaNodeKind.Schema))
                        continue;

                    foreach (var leaf in await driver.IntrospectAsync(session, child.Ref, ct))
                        if (leaf.Ref.Kind is SchemaNodeKind.Table
                            && leaf.Ref.Name.Equals(table, StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            }

            return false;
        }
    }
}

/// Runs the copies once, a little after the seed scripts: a script that creates a table should have
/// had its chance before this decides the table is missing.
public sealed class SeedFromStartup(SeedFromConnection seeds, ILogger<SeedFromStartup> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!seeds.Configured) return;

        // Same reason the seed scripts try again: the source is a container too, and a copy that
        // ran once twelve seconds in and never again leaves the target empty for the whole session.
        // Trying again is safe, because a table that exists is left alone.
        var waits = new[] { 12, 15, 15, 30, 30, 60 };

        try
        {
            foreach (var wait in waits)
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), stoppingToken);

                var sweep = await seeds.SweepAsync(stoppingToken);
                if (sweep.Pending == 0) return;

                log.LogInformation("{Pending} table(s) could not be copied yet; trying again",
                    sweep.Pending);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down first is not a failure.
        }
        catch (Exception e)
        {
            log.LogWarning(e, "the seed-from run failed");
        }
    }
}
