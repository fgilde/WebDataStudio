using System.Security.Cryptography;
using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// Where the seed scripts are, if anywhere. Off without a path: a studio that runs SQL on start
/// without being asked is not a studio anybody should point at a database.
public sealed record SeedOptions(bool Configured, string Path)
{
    public static SeedOptions FromConfiguration(IConfiguration config)
    {
        var path = config["WDS_SEED_SQL"]?.Trim();

        return string.IsNullOrEmpty(path) ? new SeedOptions(false, "") : new SeedOptions(true, path);
    }
}

/// What one sweep did: how many connections were seeded, and how many still have a script that
/// has not run.
public sealed record SeedSweep(int Seeded, int Pending);

/// Runs a seed script once per connection, so a fresh stack comes up with data in it instead of
/// empty tables nobody can click on.
///
/// This exists for development stacks. Three rules keep it from being a foot-gun: a script runs once
/// per content (its hash is remembered), never on a read-only connection, and never on one marked
/// production — a red connection is somebody saying "not here".
public sealed class SeedScripts(
    SeedOptions options, ConnectionRegistry registry, SessionFactory factory,
    WorkspaceStore workspace, ILogger<SeedScripts> log)
{
    private const string Prefix = "seed:";

    public bool Configured => options.Configured;

    /// Seeds every connection that has a script and has not had this version of it. Returns how
    /// many connections were seeded.
    public async Task<int> RunAsync(CancellationToken ct) => (await SweepAsync(ct)).Seeded;

    /// The same, and how many connections still have a script that did not run — a server that was
    /// not up yet, which is the normal state of a SQL Server thirty seconds into a stack starting.
    public async Task<SeedSweep> SweepAsync(CancellationToken ct)
    {
        if (!options.Configured) return new SeedSweep(0, 0);

        var seeded = 0;
        var pending = 0;

        foreach (var spec in registry.All())
        {
            var script = ScriptFor(spec.Name);
            if (script is null) continue;

            if (spec.ReadOnly)
            {
                log.LogInformation("{Connection} is read-only, so its seed script was not run", spec.Name);
                continue;
            }

            // Red is the studio's convention for production. Seeding it would be the worst kind of
            // helpful.
            if (string.Equals(spec.Color, "red", StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning(
                    "{Connection} is marked as production, so its seed script was not run", spec.Name);
                continue;
            }

            var hash = Hash(script);
            if (AlreadyRan(spec.Id, hash)) continue;

            if (await ApplyAsync(spec.Id, spec.Name, script, ct))
            {
                Remember(spec.Id, hash);
                seeded++;
            }
            else
            {
                // Worth coming back for: the script exists, it has not run, and the reason is
                // usually that the database is still starting.
                pending++;
            }
        }

        return new SeedSweep(seeded, pending);
    }

    /// `WDS_SEED_SQL` is either one file — used for every connection — or a directory holding
    /// `{CONNECTION}.sql` per connection name.
    private string? ScriptFor(string connectionName)
    {
        try
        {
            // The setting may name several files or folders: what the repository ships and what an
            // app host wrote. The first one that has a script for this connection wins.
            foreach (var path in ConfiguredPaths.Split(options.Path))
            {
                if (File.Exists(path)) return File.ReadAllText(path);
                if (!Directory.Exists(path)) continue;

                var file = new[]
                {
                    Path.Combine(path, $"{connectionName}.sql"),
                    Path.Combine(path, $"{connectionName.ToLowerInvariant()}.sql"),
                }.FirstOrDefault(File.Exists);

                if (file is not null) return File.ReadAllText(file);
            }

            return null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "could not read the seed script for {Connection}", connectionName);
            return null;
        }
    }

    private async Task<bool> ApplyAsync(
        string connectionId, string name, string script, CancellationToken ct)
    {
        try
        {
            var (driver, session) = await factory.OpenAsync(connectionId, ct);
            await using (session)
            {
                string? error = null;

                // One transaction where the engine has them: half a seed is worse than none.
                var request = new ScriptRequest(script, 1, 300, Transactional: driver.Caps.Transactions);

                await foreach (var chunk in driver.ExecuteAsync(session, request, ct))
                    if (chunk is ResultChunk.Error failure) error = failure.Text;

                if (error is not null)
                {
                    log.LogWarning("the seed script for {Connection} failed: {Error}", name, error);
                    return false;
                }

                log.LogInformation("seeded {Connection}", name);
                return true;
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "could not seed {Connection}", name);
            return false;
        }
    }

    private bool AlreadyRan(string connectionId, string hash)
    {
        try
        {
            return workspace.LoadItem($"{Prefix}{connectionId}") == $"\"{hash}\"";
        }
        catch (Exception)
        {
            // No workspace: better to seed twice than to leave a fresh stack empty.
            return false;
        }
    }

    private void Remember(string connectionId, string hash)
    {
        try
        {
            workspace.SaveItem($"{Prefix}{connectionId}", $"\"{hash}\"");
        }
        catch (Exception e)
        {
            log.LogDebug(e, "could not remember that {Connection} was seeded", connectionId);
        }
    }

    /// The script's content, so editing it makes it run again and restarting does not.
    private static string Hash(string script) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..32].ToLowerInvariant();
}

/// Seeds shortly after start, once the connections are reachable.
public sealed class SeedScriptStartup(SeedScripts seeds, ILogger<SeedScriptStartup> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!seeds.Configured) return;

        // A database in the same stack is not ready when the studio is. SQL Server takes the best
        // part of a minute, and a seed that ran once eight seconds in and never again is how a
        // stack comes up with an empty schema nobody can click on.
        //
        // Trying again is safe: a script that ran is remembered by its hash and is not run twice.
        var waits = new[] { 8, 15, 15, 30, 30, 60 };

        try
        {
            foreach (var wait in waits)
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), stoppingToken);

                var sweep = await seeds.SweepAsync(stoppingToken);
                if (sweep.Pending == 0) return;

                log.LogInformation(
                    "{Pending} connection(s) still have a seed script that has not run; trying again",
                    sweep.Pending);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down first is not a failure.
        }
        catch (Exception e)
        {
            log.LogWarning(e, "the seed run failed");
        }
    }
}
