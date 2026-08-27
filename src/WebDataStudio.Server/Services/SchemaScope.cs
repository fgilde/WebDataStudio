using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// Which schemas a connection reads.
///
/// A server with five thousand tables makes every studio pay for all of them: the tree's first level,
/// the completion cache, the object search and the schema snapshot each walk what they are given. On
/// such a server somebody works in two schemas and would like to say so.
///
/// Empty means everything, which is the right default: a scope somebody has to configure before the
/// studio is useful would be a worse studio.
public sealed class SchemaScope(IConfiguration config, WorkspaceStore workspace)
{
    /// `WDS_CONN_<NAME>_SCHEMAS=public,sales` — the deployment's own answer, and the one a person
    /// cannot override away, because it is usually there for a reason.
    private const string Suffix = "_SCHEMAS";

    public IReadOnlyList<string> FromEnvironment(string connectionName) =>
        Split(config[$"WDS_CONN_{connectionName.ToUpperInvariant()}{Suffix}"]);

    /// What somebody chose in this studio, which is workspace state like a layout: it belongs to the
    /// studio rather than to the connection, because two people can want different scopes.
    public IReadOnlyList<string> Chosen(string connectionId) =>
        workspace.LoadItem($"schemas:{connectionId}") is { Length: > 0 } json
            ? JsonSerializer.Deserialize<string[]>(json) ?? []
            : [];

    public void Choose(string connectionId, IReadOnlyList<string> schemas) =>
        workspace.SaveItem($"schemas:{connectionId}", JsonSerializer.Serialize(schemas));

    /// The scope in force: the environment's if it says anything, otherwise the studio's choice.
    public IReadOnlyList<string> InForce(ConnectionSpecName connection)
    {
        var configured = FromEnvironment(connection.Name);
        return configured.Count > 0 ? configured : Chosen(connection.Id);
    }

    /// Keeps only the schemas in scope. Anything that is not a schema — a folder, a key space, a
    /// container — passes through: a scope is about schemas, and filtering the rest would quietly
    /// break every other engine.
    public IReadOnlyList<SchemaNode> Filter(
        ConnectionSpecName connection, IReadOnlyList<SchemaNode> nodes)
    {
        var scope = InForce(connection);
        if (scope.Count == 0) return nodes;

        return nodes
            .Where(node => node.Ref.Kind is not (SchemaNodeKind.Schema or SchemaNodeKind.Database)
                           || scope.Contains(node.Label, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// The two things a scope needs from a connection, so nothing here depends on the whole spec.
public sealed record ConnectionSpecName(string Id, string Name);
