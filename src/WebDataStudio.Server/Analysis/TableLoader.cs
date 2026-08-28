using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// The tables a statement mentions, described.
///
/// Advice about an index needs to know which columns and indexes a table already has, and a
/// statement names tables rather than pointing at them. This walks the tree until it has found the
/// ones the statement mentions — which is what both the analyse endpoint and the capture's advice
/// need, so it lives here rather than in one of them.
public static class TableLoader
{
    /// A cap on the walk: a server with five thousand schemas must not turn one piece of advice into
    /// an afternoon of introspection.
    private const int MaxNodes = 100;

    public static async Task<Dictionary<string, ObjectDetail>> LoadAsync(IDbDriver driver,
        IDbSession session, string sql, CancellationToken ct)
    {
        var wanted = PredicateExtractor.Aliases(sql).Values
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return [];

        var found = new Dictionary<string, ObjectDetail>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<SchemaNodeRef?>();
        queue.Enqueue(null);
        var visited = 0;

        while (queue.Count > 0 && visited++ < MaxNodes && found.Count < wanted.Count)
        {
            var parent = queue.Dequeue();
            IReadOnlyList<SchemaNode> nodes;
            try { nodes = await driver.IntrospectAsync(session, parent, ct); }
            catch (Exception) { continue; }

            foreach (var node in nodes)
            {
                if (node.Ref.Kind == SchemaNodeKind.Table)
                {
                    if (!wanted.Contains(node.Ref.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (found.ContainsKey(node.Ref.Name)) continue;

                    try { found[node.Ref.Name] = await driver.DescribeAsync(session, node.Ref, ct); }
                    catch (Exception) { /* a table we cannot describe simply gets no advice */ }
                    continue;
                }

                if (node.HasChildren && node.Ref.Kind is not (SchemaNodeKind.Table or SchemaNodeKind.View))
                    queue.Enqueue(node.Ref);
            }
        }

        return found;
    }
}
