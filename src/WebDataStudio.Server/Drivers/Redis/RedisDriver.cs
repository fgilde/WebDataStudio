using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StackExchange.Redis;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.Redis;

public sealed class RedisDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => name;
    public override string ParameterPrefix => "";
    public override string Paginate(string sql, int offset, int limit) => sql;

    /// Redis has no SQL to classify, so read-only is an allow-list of commands.
    public override bool IsReadOnlyStatement(string sql) => RedisCommands.IsRead(sql);
}

public static class RedisCommands
{
    private static readonly HashSet<string> Reads = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "MGET", "STRLEN", "EXISTS", "TTL", "PTTL", "TYPE", "KEYS", "SCAN", "RANDOMKEY",
        "HGET", "HGETALL", "HKEYS", "HVALS", "HLEN", "HMGET", "HEXISTS", "HSCAN",
        "LRANGE", "LLEN", "LINDEX", "SMEMBERS", "SCARD", "SISMEMBER", "SSCAN", "SRANDMEMBER",
        "ZRANGE", "ZREVRANGE", "ZCARD", "ZSCORE", "ZCOUNT", "ZRANGEBYSCORE", "ZSCAN",
        "DBSIZE", "INFO", "PING", "TIME", "MEMORY", "OBJECT", "CLIENT", "COMMAND", "CONFIG",
        "XRANGE", "XLEN", "GETRANGE", "BITCOUNT", "PFCOUNT", "LPOS",
    };

    public static bool IsRead(string command)
    {
        var head = command.TrimStart().Split([' ', '\t', '\n'], 2)[0];
        return Reads.Contains(head);
    }
}

public sealed class RedisSession(ConnectionSpec spec, IConnectionMultiplexer multiplexer, int database) : IDbSession
{
    public ConnectionSpec Spec { get; } = spec;
    public IConnectionMultiplexer Multiplexer { get; } = multiplexer;
    public int DatabaseNumber { get; } = database;

    public IDatabase Database => Multiplexer.GetDatabase(DatabaseNumber);
    public IServer Server => Multiplexer.GetServer(Multiplexer.GetEndPoints()[0]);

    public DbConnection Connection =>
        throw new NotSupportedException("Redis does not expose an ADO.NET connection");

    public async ValueTask DisposeAsync() => await Multiplexer.CloseAsync();
}

public sealed class RedisDriver : IDbDriver
{
    private const int ScanPageSize = 500;
    private const int MaxKeys = 5000;

    public DriverInfo Info { get; } = new("redis", "Redis", 6379, "redis://localhost:6379");

    public DriverCapabilities Caps { get; } = new()
    {
        // A key space browses now: PageAsync answers with keys, and a key with its own contents.
        Sql = false, TabularBrowse = true, BrowseContainers = true, MultiDatabase = true, Backup = true,
        SessionList = true, KillSession = true, ServerStats = true, SystemCommands = true,
    };

    public SqlDialect Dialect { get; } = new RedisDialect();

    public async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var options = ConfigurationOptions.Parse(Normalize(spec.ConnectionString));
        options.AllowAdmin = true; // needed for SCAN over the keyspace and for INFO
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
        return new RedisSession(spec, multiplexer, options.DefaultDatabase ?? 0);
    }

    /// StackExchange.Redis does not read redis:// URLs; translate to host:port form.
    private static string Normalize(string connectionString)
    {
        if (!connectionString.StartsWith("redis://", StringComparison.OrdinalIgnoreCase)
            && !connectionString.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var url = new Uri(connectionString);
        var port = url.IsDefaultPort ? 6379 : url.Port;
        var options = $"{url.Host}:{port}";

        if (url.UserInfo is { Length: > 0 })
        {
            var parts = url.UserInfo.Split(':', 2);
            if (parts.Length > 1) options += $",password={Uri.UnescapeDataString(parts[1])}";
        }

        if (url.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase)) options += ",ssl=true";

        var database = url.AbsolutePath.Trim('/');
        if (database.Length > 0 && int.TryParse(database, out _)) options += $",defaultDatabase={database}";

        return options;
    }

    public async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(IDbSession session, SchemaNodeRef? parent,
        CancellationToken ct)
    {
        var redis = Cast(session);

        // The root lists databases; below that, keys are grouped by their ':' prefix, which is the
        // convention every Redis codebase uses in place of tables.
        if (parent is null)
        {
            var count = redis.Server.DatabaseCount;
            return Enumerable.Range(0, Math.Max(count, 1))
                .Select(i => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [i.ToString()]),
                    $"db{i}", true))
                .ToList();
        }

        var prefix = parent.Kind == SchemaNodeKind.Schema
            ? ""
            : string.Join(":", parent.Path.Skip(1)) + ":";

        var database = int.TryParse(parent.Path[0], out var number) ? number : 0;
        var keys = await ScanAsync(redis, database, prefix + "*", ct);

        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        var leaves = new List<SchemaNode>();

        foreach (var key in keys)
        {
            var rest = key[prefix.Length..];
            var separator = rest.IndexOf(':');

            if (separator > 0)
            {
                var group = rest[..separator];
                groups[group] = groups.GetValueOrDefault(group) + 1;
                continue;
            }

            leaves.Add(new SchemaNode(
                new SchemaNodeRef(SchemaNodeKind.Table, [.. parent.Path.Skip(parent.Kind == SchemaNodeKind.Schema ? 1 : 1).Prepend(parent.Path[0]), rest]),
                rest, false));
        }

        var folders = groups
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SchemaNode(
                new SchemaNodeRef(SchemaNodeKind.TableFolder, [.. parent.Path, g.Key]),
                g.Key, true, $"{g.Value} keys"));

        return folders.Concat(leaves.OrderBy(l => l.Label, StringComparer.Ordinal)).ToList();
    }

    public async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var redis = Cast(session);
        var database = int.TryParse(target.Path[0], out var number) ? number : 0;
        var key = string.Join(":", target.Path.Skip(1));

        var db = redis.Multiplexer.GetDatabase(database);
        var type = await db.KeyTypeAsync(key);
        var ttl = await db.KeyTimeToLiveAsync(key);
        var length = await LengthAsync(db, key, type);

        var columns = new List<ColumnInfo>
        {
            new("key", "string", false, null, true, false, null, 1),
            new("type", type.ToString().ToLowerInvariant(), false, null, false, false, null, 2),
            new("ttl", ttl is null ? "none" : $"{ttl.Value.TotalSeconds:F0}s", true, null, false, false, null, 3),
        };

        _ = ct;
        return new ObjectDetail(target, columns, [], [], [], length, null,
            $"{type.ToString().ToLowerInvariant()} key", null);
    }

    /// A page of rows out of a key space. A database or a prefix folder answers with its keys — type,
    /// expiry, length, memory — and a single key with whatever its type makes into rows.
    public async Task<TabularPage?> PageAsync(IDbSession session, SchemaNodeRef target,
        PageQuery query, CancellationToken ct)
    {
        if (target.Path.Count == 0) return null;

        var redis = Cast(session);
        var database = int.TryParse(target.Path[0], out var number) ? number : 0;
        var db = redis.Multiplexer.GetDatabase(database);

        if (target.Kind == SchemaNodeKind.Table)
            return await RedisPage.ValueAsync(db, string.Join(":", target.Path.Skip(1)), query, ct);

        var prefix = target.Path.Count > 1 ? string.Join(":", target.Path.Skip(1)) + ":" : "";
        return await RedisPage.KeysAsync(db, redis.Server, database, prefix, query, ct);
    }

    public async IAsyncEnumerable<ResultChunk> ExecuteAsync(IDbSession session, ScriptRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var redis = Cast(session);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var statements = request.Sql
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];

            if (session.Spec.ReadOnly && !RedisCommands.IsRead(statement))
            {
                yield return new ResultChunk.Error(index,
                    "this connection is read-only; the command was not executed", "WDS_READONLY", null, null);
                yield break;
            }

            var (documents, error) = await RunAsync(redis, statement, ct);

            if (error is not null)
            {
                yield return new ResultChunk.Error(index, error, null, null, null);
                continue;
            }

            if (documents.Count > 0) yield return new ResultChunk.Documents(index, documents);
            yield return new ResultChunk.End(index, 0, watch.ElapsedMilliseconds, false);
        }
    }

    private static async Task<(List<JsonElement> Documents, string? Error)> RunAsync(RedisSession redis,
        string statement, CancellationToken ct)
    {
        var parts = Split(statement);
        if (parts.Count == 0) return ([], "empty command");

        try
        {
            var result = await redis.Database.ExecuteAsync(parts[0],
                parts.Skip(1).Cast<object>().ToArray());

            _ = ct;
            return ([Wrap(result)], null);
        }
        catch (RedisException e)
        {
            return ([], e.Message);
        }
    }

    private static JsonElement Wrap(RedisResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(Convert(result))).RootElement.Clone();

    private static object? Convert(RedisResult result)
    {
        if (result.IsNull) return null;

        return result.Resp2Type switch
        {
            ResultType.Integer => (long)result,
            ResultType.Array => ((RedisResult[])result!).Select(Convert).ToList(),
            _ => result.ToString(),
        };
    }

    /// Splits a command line, honouring quoted arguments the way redis-cli does.
    private static List<string> Split(string command)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var quote = '"';

        foreach (var c in command)
        {
            if (inQuotes)
            {
                if (c == quote) { inQuotes = false; continue; }
                current.Append(c);
                continue;
            }

            switch (c)
            {
                case '"' or '\'': inQuotes = true; quote = c; break;
                case ' ' or '\t':
                    if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                    break;
                default: current.Append(c); break;
            }
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    public Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct) =>
        throw new NotSupportedException("Redis has no query planner");

    public async Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope, SchemaNodeRef? target,
        CancellationToken ct)
    {
        var redis = Cast(session);
        var findings = new List<AnalyzeFinding>();

        var keys = await ScanAsync(redis, 0, "*", ct);
        var withoutTtl = 0;

        foreach (var key in keys.Take(1000))
            if (await redis.Database.KeyTimeToLiveAsync(key) is null) withoutTtl++;

        if (withoutTtl > 0)
            findings.Add(new AnalyzeFinding("no-expiry", "info",
                $"{withoutTtl} keys have no TTL",
                "Keys without an expiry stay until something deletes them; on a cache that is usually unintended.",
                null));

        if (keys.Count >= MaxKeys)
            findings.Add(new AnalyzeFinding("large-keyspace", "info",
                $"The keyspace has at least {MaxKeys} keys",
                "The explorer stops scanning at this point so a large keyspace cannot stall the UI.",
                null));

        return new AnalyzeReport(findings);
    }

    private static async Task<List<string>> ScanAsync(RedisSession redis, int database, string pattern,
        CancellationToken ct)
    {
        var keys = new List<string>();

        // SCAN, never KEYS: KEYS blocks the server for the whole sweep.
        foreach (var key in redis.Server.Keys(database, pattern, ScanPageSize))
        {
            ct.ThrowIfCancellationRequested();
            keys.Add(key.ToString());
            if (keys.Count >= MaxKeys) break;
        }

        await Task.CompletedTask;
        return keys;
    }

    private static async Task<long?> LengthAsync(IDatabase db, string key, RedisType type) => type switch
    {
        RedisType.String => await db.StringLengthAsync(key),
        RedisType.Hash => await db.HashLengthAsync(key),
        RedisType.List => await db.ListLengthAsync(key),
        RedisType.Set => await db.SetLengthAsync(key),
        RedisType.SortedSet => await db.SortedSetLengthAsync(key),
        _ => null,
    };

    private static RedisSession Cast(IDbSession session) =>
        // Unwrap: a pooled or tunnelled session is a wrapper around the one this driver opened.
        session.Unwrap() as RedisSession
        ?? throw new InvalidOperationException("this session does not belong to the Redis driver");
}
