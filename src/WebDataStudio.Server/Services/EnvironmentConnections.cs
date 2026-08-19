using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// Reads connections defined in the environment. These are merged in on every start and are
/// read-only in the UI, so a redeploy always reflects the current environment.
public static class EnvironmentConnections
{
    private const string ArrayVariable = "WDS_CONNECTIONS";
    private const string SinglePrefix = "WDS_CONN_";

    /// Reserved endings: `WDS_CONN_SHOP_ENGINE` configures `WDS_CONN_SHOP`, it is not a
    /// connection called SHOP_ENGINE.
    private static readonly string[] Suffixes = ["_ENGINE", "_READONLY", "_COLOR", "_GROUP"];

    private sealed record Entry(string Name, string? Engine, string ConnectionString,
        bool ReadOnly, string? Color, string? Group);

    public static IReadOnlyList<ConnectionSpec> Parse(IDictionary<string, string?> env)
    {
        var specs = new List<ConnectionSpec>();

        if (env.TryGetValue(ArrayVariable, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<Entry>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Engine)) continue;
                    specs.Add(new ConnectionSpec(StableId(e.Name), e.Name, e.Engine!, e.ConnectionString,
                        e.ReadOnly, e.Color, e.Group, ConnectionSource.Environment));
                }
            }
            catch (JsonException)
            {
                // A malformed array must not stop the server from starting; the individual
                // WDS_CONN_* variables below are still usable.
            }
        }

        foreach (var (key, value) in env)
        {
            if (!key.StartsWith(SinglePrefix, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value)) continue;

            // The key as configuration sees it: ASP.NET's environment provider turns a double
            // underscore into a colon, so WDS_CONN_ABP___SPARK arrives as WDS_CONN_ABP:_SPARK.
            // Lookups use that spelling; the name shown to a user gets its underscores back.
            var variable = key[SinglePrefix.Length..];
            var name = variable.Replace(":", "__", StringComparison.Ordinal);

            // The suffixes are settings for another variable, not connections of their own.
            if (Suffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal))) continue;
            if (specs.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

            string? Setting(string suffix) =>
                env.TryGetValue($"{SinglePrefix}{variable}{suffix}", out var found) && !string.IsNullOrWhiteSpace(found)
                    ? found.Trim()
                    : null;

            var declared = Setting("_ENGINE")?.ToLowerInvariant();

            // Two accepted shapes: a URL, or a provider-native connection string plus an engine.
            // The second is what an orchestrator hands over, because that is what its resources
            // already produce.
            string engine;
            string connectionString;

            if (Uri.TryCreate(value, UriKind.Absolute, out var url)
                && ConnectionUrl.EngineFromScheme(url.Scheme) is { } fromScheme)
            {
                engine = declared ?? fromScheme;
                connectionString = engine == fromScheme
                    ? ConnectionUrl.ToAdoConnectionString(engine, url)
                    : value;
            }
            else
            {
                if ((declared ?? EngineGuess.FromConnectionString(value)) is not { } guessed) continue;
                engine = guessed;
                connectionString = value;
            }

            specs.Add(new ConnectionSpec(StableId(name), name, engine, connectionString,
                ReadOnly: string.Equals(Setting("_READONLY"), "true", StringComparison.OrdinalIgnoreCase),
                Color: Setting("_COLOR"),
                Group: Setting("_GROUP"),
                ConnectionSource.Environment));
        }

        return specs;
    }

    /// Deterministic id so bookmarks, tabs and saved layouts survive a container restart.
    private static string StableId(string name) =>
        "env-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
}
