using System.Data.Common;
using ColumnInfo = WebDataStudio.Server.Drivers.Abstractions.ColumnInfo;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.DuckDb;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Drivers.Storage;

/// Object storage as a connection: S3-compatible, Azure Blob, Google Cloud Storage, or a folder.
///
/// The tree comes from the store, one page at a time, because a container with a million objects is
/// the one thing that must never be walked. The SQL comes from the DuckDB the session holds, so a
/// Parquet in a bucket is a table like any other — with the grid, the filter language, the plan
/// panel, export and masking all working without knowing where the rows came from.
public sealed class StorageDriver : AdoDriverBase
{
    /// One page of a listing. Big enough that most folders arrive whole, small enough that a
    /// container nobody should have made this way does not stall the tree.
    private const int PageSize = 500;

    private readonly DuckDbDriver _duckDb = new();

    public override DriverInfo Info { get; } =
        new("storage", "Object storage", 0, "s3://bucket/prefix?region=eu-central-1");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true,
        TabularBrowse = true,
        EstimatedPlan = true,
        ActualPlan = true,
        // A file is not a table: no DDL, no transactions, no schemas, no keys. Everything the UI
        // would offer on that basis stays hidden.
    };

    public override SqlDialect Dialect { get; } = new DuckDbDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct) =>
        await StorageSession.OpenAsync(spec, ct);

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct, bool systemObjects = false)
    {
        var store = StoreOf(session);

        if (parent is null)
        {
            // The connection is scoped to one container, and to a prefix inside it where the URL
            // said so. That scope is the root of the tree; nothing above it is reachable.
            var target = store.Target;

            // A folder's "container" is its whole path, and a tree row is not the place for it: the
            // folder's own name reads as a name, and the path belongs in the line underneath.
            var name = target.Provider == StorageProvider.Local
                ? Path.GetFileName(target.Container.TrimEnd('/', '\\')) is { Length: > 0 } folder
                    ? folder
                    : target.Container
                : target.Container;

            var label = target.Prefix.Length == 0 ? name : $"{name}/{target.Prefix}";
            var detail = target.Provider == StorageProvider.Local
                ? target.Container
                : target.Provider.ToString();

            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Container, [target.Container]), label,
                    true, Detail: detail),
            ];
        }

        if (parent.Kind is not (SchemaNodeKind.Container or SchemaNodeKind.Prefix
            or SchemaNodeKind.StorageMore))
            return [];

        // A "load more" node carries the cursor as its last segment; everything before it is the
        // prefix that was being listed.
        var isMore = parent.Kind == SchemaNodeKind.StorageMore;
        var segments = parent.Path.Skip(1).ToList();
        var cursor = isMore ? Uri.UnescapeDataString(segments[^1]) : null;
        if (isMore) segments.RemoveAt(segments.Count - 1);

        var prefix = string.Join('/', segments);
        var page = await store.ListAsync(prefix, cursor, PageSize, ct);
        var basePath = parent.Path.Take(1).Concat(segments).ToList();

        var nodes = new List<SchemaNode>();

        foreach (var entry in page.Entries)
        {
            var path = basePath.Append(entry.Name).ToList();

            nodes.Add(entry.IsPrefix
                ? new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Prefix, path), entry.Name, true)
                : new SchemaNode(new SchemaNodeRef(SchemaNodeKind.StorageObject, path), entry.Name,
                    false, Describe(entry)));
        }

        if (page.Cursor is { Length: > 0 } next)
            nodes.Add(new SchemaNode(
                new SchemaNodeRef(SchemaNodeKind.StorageMore,
                    basePath.Append(Uri.EscapeDataString(next)).ToList()),
                "Load more…", true));

        return nodes;
    }

    public override async Task<ObjectDetail> DescribeAsync(
        IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var store = StoreOf(session);
        var key = KeyOf(target);
        var head = await store.HeadAsync(key, ct);

        var columns = new List<ColumnInfo>();
        long? rows = null;

        if (FromClause(session, target) is { } from)
        {
            // What the reader sees, asked of the reader rather than guessed from the name. A file
            // that turns out not to be what its extension says still has its details below.
            try
            {
                columns.AddRange(await ColumnsAsync(session, from, ct));
                rows = await ParquetRowsAsync(session, store.SqlUri(key), ct);
            }
            catch (DbException)
            {
                // Unreadable as a table. The preview and the download still work.
            }
        }

        return new ObjectDetail(target, columns, [], [], [], rows, head?.SizeBytes,
            Summary(head), Ddl: null);
    }

    /// What to select from: a reader over the object, which is where a table's qualified name would
    /// otherwise be. A folder needs a pattern to say which of its files belong together, so it is
    /// queryable once one is given — "Query as table…" asks for it.
    public override string? FromClause(IDbSession session, SchemaNodeRef target)
    {
        if (target.Kind is not (SchemaNodeKind.StorageObject or SchemaNodeKind.Prefix)) return null;

        var key = KeyOf(target);
        if (target.Kind == SchemaNodeKind.Prefix && !key.Contains('*')) return null;

        return StorageReader.Call(StoreOf(session).SqlUri(key));
    }

    public override Task<PlanNode> ExplainAsync(
        IDbSession session, string sql, PlanMode mode, CancellationToken ct) =>
        // The engine underneath is DuckDB, so the plan is DuckDB's, read exactly as it is there.
        _duckDb.ExplainAsync(session, sql, mode, ct);

    private static async Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(
        IDbSession session, string from, CancellationToken ct)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM {from}";

        var columns = new List<ColumnInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            columns.Add(new ColumnInfo(reader.GetString(0), reader.GetString(1),
                reader.GetValue(2).ToString() is "YES" or "True" or "true", null,
                false, false, null, columns.Count + 1));

        return columns;
    }

    /// A Parquet says how many rows it has in its footer, which costs nothing to read. Anything else
    /// would have to be counted, and a count is a query somebody can run when they want one.
    private static async Task<long?> ParquetRowsAsync(
        IDbSession session, string uri, CancellationToken ct)
    {
        if (!uri.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)) return null;

        await using var command = session.Connection.CreateCommand();
        command.CommandText =
            // Cast in SQL: the sum is a HUGEINT, which arrives as a BigInteger that Convert
            // refuses, and no Parquet has more rows than a BIGINT holds.
            $"SELECT sum(num_rows)::BIGINT FROM parquet_file_metadata('{uri.Replace("'", "''")}')";

        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// The object's own facts, in the line the detail panel shows: content type, when it changed,
    /// its ETag and its storage class.
    private static string? Summary(StorageObject? head)
    {
        if (head is null) return null;

        var parts = new List<string>();
        if (head.ContentType is { Length: > 0 } type) parts.Add(type);
        if (head.Modified is { } modified) parts.Add($"modified {modified:u}");
        if (head.ETag is { Length: > 0 } etag) parts.Add($"ETag {etag.Trim('"')}");
        if (head.StorageClass is { Length: > 0 } storageClass) parts.Add(storageClass);

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Describe(StorageEntry entry)
    {
        var parts = new List<string>();
        if (entry.SizeBytes is { } size) parts.Add(Size(size));
        if (entry.Modified is { } modified) parts.Add(modified.ToString("yyyy-MM-dd"));
        return string.Join(" · ", parts);
    }

    private static string Size(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// The key inside the connection's scope: the tree's first segment is the container.
    public static string KeyOf(SchemaNodeRef target) => string.Join('/', target.Path.Skip(1));

    private static IObjectStore StoreOf(IDbSession session) =>
        session.Unwrap() is StorageSession storage
            ? storage.Store
            : throw new InvalidOperationException("not a storage session");
}
