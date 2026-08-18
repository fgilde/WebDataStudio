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

            var name = key[SinglePrefix.Length..];
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) continue;
            if (ConnectionUrl.EngineFromScheme(url.Scheme) is not { } engine) continue;
            if (specs.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

            specs.Add(new ConnectionSpec(StableId(name), name, engine,
                ConnectionUrl.ToAdoConnectionString(engine, url),
                ReadOnly: false, Color: null, Group: null, ConnectionSource.Environment));
        }

        return specs;
    }

    /// Deterministic id so bookmarks, tabs and saved layouts survive a container restart.
    private static string StableId(string name) =>
        "env-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
}
