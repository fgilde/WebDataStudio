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
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!options.Configured) return 0;

        var seeded = 0;

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
        }

        return seeded;
    }

    /// `WDS_SEED_SQL` is either one file — used for every connection — or a directory holding
    /// `{CONNECTION}.sql` per connection name.
    private string? ScriptFor(string connectionName)
    {
        try
        {
            if (File.Exists(options.Path)) return File.ReadAllText(options.Path);
            if (!Directory.Exists(options.Path)) return null;

            var candidates = new[]
            {
                Path.Combine(options.Path, $"{connectionName}.sql"),
                Path.Combine(options.Path, $"{connectionName.ToLowerInvariant()}.sql"),
            };

            var file = candidates.FirstOrDefault(File.Exists);
            return file is null ? null : File.ReadAllText(file);
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

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
            await seeds.RunAsync(stoppingToken);
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
