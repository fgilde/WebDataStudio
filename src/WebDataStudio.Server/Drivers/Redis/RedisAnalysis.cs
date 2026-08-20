using StackExchange.Redis;

namespace WebDataStudio.Server.Drivers.Redis;

public sealed record PrefixStat(string Prefix, long Keys, long Bytes);
public sealed record TypeStat(string Type, long Keys, long Bytes);

/// What a keyspace is made of: which prefixes hold the memory, which types dominate, the biggest
/// keys, and what is about to expire. Sampled rather than complete — the point is to find the
/// prefix that grew, and reading a million keys to answer that would be the problem it is looking
/// for.
public sealed record KeyspaceAnalysisDto(
    long SampledKeys,
    bool Complete,
    IReadOnlyList<PrefixStat> Prefixes,
    IReadOnlyList<TypeStat> Types,
    IReadOnlyList<KeyDto> Largest,
    IReadOnlyList<KeyDto> ExpiringSoon,
    long? TotalMemoryBytes,
    long? TotalKeys);

/// The numbers behind the analysis panel. SCAN and MEMORY USAGE only: KEYS and DEBUG OBJECT are the
/// two commands that turn "let me look at this" into an incident.
public static class RedisAnalysis
{
    public const int DefaultSample = 5_000;
    private const int TopN = 20;

    public static async Task<KeyspaceAnalysisDto> RunAsync(
        IConnectionMultiplexer multiplexer, int database, int sample, CancellationToken ct)
    {
        var keys = new List<KeyDto>();
        long cursor = 0;

        while (keys.Count < sample)
        {
            var page = await RedisKeyspace.ScanAsync(
                multiplexer, database, null, null, cursor, 1_000, withSize: true, ct);

            keys.AddRange(page.Keys);
            cursor = page.NextCursor;

            if (cursor == 0) break;
        }

        var distinct = keys
            .GroupBy(key => key.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        // A scan answers in buckets, so the last page can overshoot the sample. The numbers are
        // reported for what was actually looked at, and "complete" means the whole keyspace was —
        // reached the end of the cursor and nothing had to be dropped.
        var sampled = distinct.Take(sample).ToList();
        var complete = cursor == 0 && sampled.Count == distinct.Count;

        var prefixes = sampled
            .GroupBy(PrefixOf, StringComparer.Ordinal)
            .Select(group => new PrefixStat(group.Key, group.Count(), group.Sum(key => key.SizeBytes ?? 0)))
            .OrderByDescending(stat => stat.Bytes)
            .ThenByDescending(stat => stat.Keys)
            .Take(TopN)
            .ToList();

        var types = sampled
            .GroupBy(key => key.Type, StringComparer.Ordinal)
            .Select(group => new TypeStat(group.Key, group.Count(), group.Sum(key => key.SizeBytes ?? 0)))
            .OrderByDescending(stat => stat.Keys)
            .ToList();

        var largest = sampled
            .Where(key => key.SizeBytes is not null)
            .OrderByDescending(key => key.SizeBytes)
            .Take(TopN)
            .ToList();

        var expiring = sampled
            .Where(key => key.TtlSeconds is not null)
            .OrderBy(key => key.TtlSeconds)
            .Take(TopN)
            .ToList();

        var db = multiplexer.GetDatabase(database);
        var used = await db.ExecuteAsync("INFO", "memory");
        var total = await db.ExecuteAsync("DBSIZE");

        return new KeyspaceAnalysisDto(
            sampled.Count, complete, prefixes, types, largest, expiring,
            UsedMemory((string?)used), (long)total);
    }

    /// The part before the first separator, which is how every Redis codebase names its keys.
    /// A key without one is its own group, so a flat keyspace still says something.
    private static string PrefixOf(KeyDto key)
    {
        var separator = key.Key.IndexOfAny([':', '/', '|']);
        return separator > 0 ? key.Key[..separator] : "(no prefix)";
    }

    /// `used_memory` out of INFO memory. Parsed rather than requested on its own, because INFO is
    /// one round trip and MEMORY STATS is not available everywhere.
    private static long? UsedMemory(string? info)
    {
        if (info is null) return null;

        foreach (var line in info.Split('\n'))
        {
            if (!line.StartsWith("used_memory:", StringComparison.Ordinal)) continue;
            if (long.TryParse(line["used_memory:".Length..].Trim(), out var bytes)) return bytes;
        }

        return null;
    }
}
