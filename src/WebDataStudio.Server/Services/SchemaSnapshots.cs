using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One table as a snapshot remembers it: names and shapes, nothing that changes by itself.
public sealed record TableShape(
    string Ref, IReadOnlyList<string> Columns, IReadOnlyList<string> Indexes,
    IReadOnlyList<string> ForeignKeys);

public sealed record SchemaShape(
    string ConnectionId, string ConnectionName, DateTimeOffset At, IReadOnlyList<TableShape> Tables);

/// What changed between two snapshots. Empty means the schema is where it was.
public sealed record SchemaDrift(
    DateTimeOffset? Before, DateTimeOffset After,
    IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
    public bool Any => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;

    public string Summary => Any
        ? string.Join(", ", new[]
        {
            Added.Count > 0 ? $"{Added.Count} added" : null,
            Removed.Count > 0 ? $"{Removed.Count} removed" : null,
            Changed.Count > 0 ? $"{Changed.Count} changed" : null,
        }.Where(part => part is not null))
        : "no change";
}

/// Where snapshots are kept, if anywhere. Off without a directory: writing files somewhere nobody
/// asked for is not a thing a studio should do on its own.
public sealed record SnapshotOptions(bool Configured, string Directory)
{
    public static SnapshotOptions FromConfiguration(IConfiguration config)
    {
        var directory = config["WDS_SCHEMA_SNAPSHOT_DIR"]?.Trim();

        return string.IsNullOrEmpty(directory)
            ? new SnapshotOptions(false, "")
            : new SnapshotOptions(true, directory);
    }
}

/// Takes a snapshot of every connection's schema at start and says what moved since the last one.
///
/// The point is drift nobody meant: a column added by hand on staging, an index dropped by a
/// migration that was not supposed to touch it. The studio knows the schema anyway — this only
/// writes it down and compares.
public sealed class SchemaSnapshots(
    SnapshotOptions options, ConnectionRegistry registry, SessionFactory factory,
    HealthAlertSink alerts, ILogger<SchemaSnapshots> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// The last drift per connection id, for the endpoint to report.
    private readonly Dictionary<string, SchemaDrift> _drift = [];

    public bool Configured => options.Configured;

    public SchemaDrift? DriftOf(string connectionId) =>
        _drift.TryGetValue(connectionId, out var drift) ? drift : null;

    /// Snapshots every connection and records the drift. Returns how many connections moved.
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        if (!options.Configured) return 0;

        Directory.CreateDirectory(options.Directory);
        var moved = 0;

        foreach (var spec in registry.All())
        {
            try
            {
                var before = Read(spec.Id);
                var after = await TakeAsync(spec.Id, spec.Name, ct);
                var drift = Compare(before, after);

                _drift[spec.Id] = drift;
                Write(spec.Id, after);

                if (!drift.Any) continue;

                moved++;
                log.LogInformation("the schema of {Connection} moved: {Summary}", spec.Name, drift.Summary);
                await alerts.SchemaDriftAsync(spec, drift, ct);
            }
            catch (Exception e)
            {
                // A connection that cannot be read is not drift; it is a connection that is down.
                log.LogDebug(e, "could not snapshot {Connection}", spec.Name);
            }
        }

        return moved;
    }

    /// The schema as it is now. Bounded like every other walk: a database with thousands of tables
    /// is where an unbounded one hurts.
    public async Task<SchemaShape> TakeAsync(string connectionId, string name, CancellationToken ct)
    {
        var (driver, session) = await factory.OpenAsync(connectionId, ct);
        await using (session)
        {
            var tables = new List<TableShape>();
            var queue = new Queue<SchemaNodeRef?>();
            queue.Enqueue(null);
            var visited = 0;

            while (queue.Count > 0 && visited++ < 200 && tables.Count < 500)
            {
                var parent = queue.Dequeue();

                foreach (var node in await driver.IntrospectAsync(session, parent, ct))
                {
                    if (node.Ref.Kind is SchemaNodeKind.Table or SchemaNodeKind.View)
                    {
                        var detail = await driver.DescribeAsync(session, node.Ref, ct);

                        tables.Add(new TableShape(
                            node.Ref.ToString(),
                            [.. detail.Columns
                                .OrderBy(column => column.Name, StringComparer.Ordinal)
                                .Select(column =>
                                    $"{column.Name} {column.DataType}{(column.Nullable ? " null" : "")}" +
                                    $"{(column.IsPrimaryKey ? " pk" : "")}")],
                            [.. detail.Indexes
                                .OrderBy(index => index.Name, StringComparer.Ordinal)
                                .Select(index =>
                                    $"{index.Name}({string.Join(",", index.Columns)})" +
                                    $"{(index.Unique ? " unique" : "")}")],
                            [.. detail.ForeignKeys
                                .OrderBy(key => key.Name, StringComparer.Ordinal)
                                .Select(key =>
                                    $"{key.Name}({string.Join(",", key.Columns)})->" +
                                    $"{key.ReferencedTable}({string.Join(",", key.ReferencedColumns)})")]));

                        continue;
                    }

                    if (node.HasChildren) queue.Enqueue(node.Ref);
                }
            }

            return new SchemaShape(connectionId, name, DateTimeOffset.UtcNow,
                [.. tables.OrderBy(table => table.Ref, StringComparer.Ordinal)]);
        }
    }

    /// What moved. A changed table is named with what about it changed, because "orders changed" is
    /// not something anybody can act on.
    public static SchemaDrift Compare(SchemaShape? before, SchemaShape after)
    {
        if (before is null) return new SchemaDrift(null, after.At, [], [], []);

        var was = before.Tables.ToDictionary(table => table.Ref, StringComparer.Ordinal);
        var now = after.Tables.ToDictionary(table => table.Ref, StringComparer.Ordinal);

        var added = now.Keys.Where(name => !was.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        var removed = was.Keys.Where(name => !now.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        var changed = new List<string>();

        foreach (var (name, table) in now.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!was.TryGetValue(name, out var old)) continue;

            var differences = new List<string>();
            Describe(differences, "column", old.Columns, table.Columns);
            Describe(differences, "index", old.Indexes, table.Indexes);
            Describe(differences, "foreign key", old.ForeignKeys, table.ForeignKeys);

            if (differences.Count > 0) changed.Add($"{name}: {string.Join("; ", differences)}");
        }

        return new SchemaDrift(before.At, after.At, added, removed, changed);
    }

    private static void Describe(
        List<string> into, string kind, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        foreach (var gone in before.Except(after, StringComparer.Ordinal))
            into.Add($"{kind} gone: {gone}");

        foreach (var fresh in after.Except(before, StringComparer.Ordinal))
            into.Add($"{kind} now: {fresh}");
    }

    private SchemaShape? Read(string connectionId)
    {
        var path = PathOf(connectionId);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<SchemaShape>(File.ReadAllText(path), Json);
        }
        catch (Exception)
        {
            // A file we cannot read is the same as no file: the next snapshot replaces it.
            return null;
        }
    }

    private void Write(string connectionId, SchemaShape shape)
    {
        // Written through a temporary file, so a snapshot interrupted halfway does not become the
        // baseline for the next comparison.
        var path = PathOf(connectionId);
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(shape, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private string PathOf(string connectionId) =>
        Path.Combine(options.Directory, $"schema-{connectionId}.json");
}

/// Runs the first sweep a little after start, when the connections are up but nobody is waiting on
/// the studio yet.
public sealed class SchemaSnapshotStartup(SchemaSnapshots snapshots) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!snapshots.Configured) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            await snapshots.SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down before the first sweep is not a failure.
        }
    }
}
