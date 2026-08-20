using StackExchange.Redis;

namespace WebDataStudio.Server.Drivers.Redis;

/// One key, as the browser shows it.
public sealed record KeyDto(string Key, string Type, long? TtlSeconds, long? SizeBytes, long? Length);

/// A page of keys plus the cursor to continue from. Zero means the scan is complete — that is
/// Redis's own protocol, kept rather than translated. The page can overshoot the requested count:
/// SCAN answers in buckets, and trimming would mean throwing keys away that the next cursor no
/// longer covers.
public sealed record KeyPageDto(IReadOnlyList<KeyDto> Keys, long NextCursor, bool Complete);

/// Reading the keyspace without ever holding it in memory.
///
/// The tree used to call SCAN until it had every key or hit a five-thousand ceiling, which is fine
/// for a demo and wrong for a real Redis: the answer was silently truncated and a large keyspace
/// took a minute to open. This issues SCAN itself and hands the cursor back, so the caller decides
/// how far to go — and the type, TTL and size of a page come back in one pipeline rather than in
/// three round trips per key.
///
/// SCAN is issued directly rather than through `IServer.Keys`, which owns its own cursor and runs
/// the whole loop internally: paging is the entire point here.
public static class RedisKeyspace
{
    /// COUNT is a hint to Redis, not a limit; asking for far more than a page wastes the trip.
    private const int MaxCount = 1000;

    /// How many SCAN round trips one page may cost. MATCH filters the bucket Redis happens to walk,
    /// not the keyspace, so a narrow pattern over a large database returns empty iterations — a
    /// caller asking for "the next twenty user:* keys" would otherwise get an empty page and a
    /// cursor, over and over. The cap keeps one request bounded even when the pattern matches
    /// nothing at all.
    private const int MaxIterations = 40;

    public static async Task<KeyPageDto> ScanAsync(
        IConnectionMultiplexer multiplexer, int database, string? match, string? type,
        long cursor, int count, bool withSize, CancellationToken ct)
    {
        var db = multiplexer.GetDatabase(database);
        var pageSize = Math.Clamp(count, 1, MaxCount);
        var serverSideType = !string.IsNullOrWhiteSpace(type);

        // COUNT is how many buckets Redis walks per trip, not how many keys come back. A narrow
        // pattern over a big keyspace needs it well above the page size, or the page ends up empty
        // while the matching key sits three iterations away.
        var scanCount = Math.Clamp(Math.Max(pageSize, 500), 1, MaxCount);

        var keys = new List<RedisKey>();
        var iterations = 0;

        while (keys.Count < pageSize && iterations < MaxIterations)
        {
            iterations++;
            ct.ThrowIfCancellationRequested();

            var arguments = new List<object> { cursor };
            if (!string.IsNullOrWhiteSpace(match)) { arguments.Add("MATCH"); arguments.Add(match); }
            arguments.Add("COUNT");
            arguments.Add(scanCount);

            // TYPE is filtered by the server since Redis 6. An older server rejects the argument
            // and the filter happens here instead; the caller sees the same answer either way.
            if (serverSideType) { arguments.Add("TYPE"); arguments.Add(type!); }

            RedisResult result;
            try
            {
                result = await db.ExecuteAsync("SCAN", arguments.ToArray());
            }
            catch (RedisServerException) when (serverSideType)
            {
                serverSideType = false;
                continue;
            }

            var parts = (RedisResult[])result!;
            cursor = long.Parse((string)parts[0]!);
            keys.AddRange(((RedisResult[])parts[1]!).Select(entry => new RedisKey((string)entry!)));

            if (cursor == 0) break;
        }

        var described = await DescribeAsync(db, keys, serverSideType ? null : type, withSize, ct);

        return new KeyPageDto(described, cursor, cursor == 0);
    }

    /// Type, TTL and — when asked for — memory of every key in the page, in one pipeline.
    private static async Task<List<KeyDto>> DescribeAsync(
        IDatabase db, List<RedisKey> keys, string? type, bool withSize, CancellationToken ct)
    {
        if (keys.Count == 0) return [];

        var types = keys.Select(key => db.KeyTypeAsync(key)).ToList();
        var ttls = keys.Select(key => db.KeyTimeToLiveAsync(key)).ToList();
        var sizes = withSize
            ? keys.Select(key => db.ExecuteAsync("MEMORY", "USAGE", key.ToString())).ToList()
            : [];

        await Task.WhenAll(types.Cast<Task>().Concat(ttls).Concat(sizes.Cast<Task>()));
        ct.ThrowIfCancellationRequested();

        var lengths = keys
            .Select((key, index) => LengthAsync(db, key, types[index].Result))
            .ToList();
        await Task.WhenAll(lengths);

        var described = new List<KeyDto>(keys.Count);

        for (var index = 0; index < keys.Count; index++)
        {
            var keyType = NameOf(types[index].Result);
            if (type is { Length: > 0 } && !keyType.Equals(type, StringComparison.OrdinalIgnoreCase))
                continue;

            var ttl = ttls[index].Result;
            long? size = null;
            if (withSize && !sizes[index].Result.IsNull) size = (long)sizes[index].Result;

            described.Add(new KeyDto(
                keys[index].ToString(),
                keyType,
                ttl is null ? null : (long)ttl.Value.TotalSeconds,
                size,
                lengths[index].Result));
        }

        return described;
    }

    /// Redis's own name for a type: TYPE answers "zset", not "sortedset", and SCAN ... TYPE expects
    /// the same spelling. Using the driver enum's name would make the filter silently match nothing.
    internal static string NameOf(RedisType type) => type switch
    {
        RedisType.SortedSet => "zset",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// What "length" means depends on the type: characters for a string, entries for everything
    /// else. A key that vanished between the scan and this call reads as null rather than throwing.
    internal static async Task<long?> LengthAsync(IDatabase db, RedisKey key, RedisType type)
    {
        try
        {
            return type switch
            {
                RedisType.String => await db.StringLengthAsync(key),
                RedisType.Hash => await db.HashLengthAsync(key),
                RedisType.List => await db.ListLengthAsync(key),
                RedisType.Set => await db.SetLengthAsync(key),
                RedisType.SortedSet => await db.SortedSetLengthAsync(key),
                RedisType.Stream => await db.StreamLengthAsync(key),
                _ => null,
            };
        }
        catch (RedisServerException)
        {
            return null;
        }
    }
}
