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
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!options.Configured) return 0;

        var copied = 0;

        foreach (var copy in Read())
        {
            if (copy.From is not { Length: > 0 } || copy.To is not { Length: > 0 }) continue;

            var target = registry.Find(copy.To);

            if (target is null)
            {
                log.LogWarning("there is no connection called {Connection} to seed", copy.To);
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
                    // One table that will not copy is not a reason for the others not to.
                    log.LogWarning(e, "{Table} could not be copied from {From} into {To}",
                        table, copy.From, copy.To);
                }
            }
        }

        return copied;
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

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
            await seeds.RunAsync(stoppingToken);
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
