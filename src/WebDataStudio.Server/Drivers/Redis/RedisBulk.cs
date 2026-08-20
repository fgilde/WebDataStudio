using StackExchange.Redis;

namespace WebDataStudio.Server.Drivers.Redis;

/// What a bulk action is about to touch: how many keys matched and a sample of them.
public sealed record BulkPreviewDto(string Hash, long MatchedKeys, IReadOnlyList<string> Sample);

/// Delete, expire or persist everything matching a pattern.
public sealed record BulkRequest(int Database, string Match, string? Type, string Action, long? TtlSeconds);

/// "Delete every session:* key" is a normal day's work with Redis and a mistake that cannot be
/// undone, so it happens in two steps: match, then apply the matched set. The keys are resolved
/// once and kept, rather than re-scanned at apply time — otherwise the set that was approved and
/// the set that is deleted are two different things.
public static class RedisBulk
{
    /// The ceiling on one bulk action. A pattern matching more than this is a sign the pattern is
    /// wrong, and a studio that happily deletes a million keys in one request is a footgun.
    public const int MaxKeys = 100_000;

    public static async Task<List<string>> MatchAsync(
        IConnectionMultiplexer multiplexer, int database, string match, string? type,
        CancellationToken ct)
    {
        var keys = new List<string>();
        long cursor = 0;

        do
        {
            var page = await RedisKeyspace.ScanAsync(
                multiplexer, database, match, type, cursor, 1000, withSize: false, ct);

            keys.AddRange(page.Keys.Select(key => key.Key));
            cursor = page.NextCursor;

            if (keys.Count > MaxKeys)
                throw new InvalidOperationException(
                    $"the pattern matches more than {MaxKeys:N0} keys; narrow it down");
        }
        while (cursor != 0);

        // SCAN can hand out the same key twice; deleting it twice is harmless, counting it twice
        // is a lie in the preview.
        return [.. keys.Distinct(StringComparer.Ordinal)];
    }

    public static async Task<long> ApplyAsync(
        IConnectionMultiplexer multiplexer, BulkRequest request, IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        var db = multiplexer.GetDatabase(request.Database);
        var affected = 0L;

        // Batches rather than one pipeline of a hundred thousand commands: a batch that large ties
        // up the connection long enough to look like an outage to everything else using it.
        foreach (var chunk in keys.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();

            var pending = request.Action.ToLowerInvariant() switch
            {
                "delete" => chunk.Select(key => db.KeyDeleteAsync(key)).ToArray(),
                "expire" => chunk.Select(key => db.KeyExpireAsync(key,
                    TimeSpan.FromSeconds(request.TtlSeconds ?? 0))).ToArray(),
                "persist" => chunk.Select(key => db.KeyPersistAsync(key)).ToArray(),
                _ => throw new ArgumentException($"'{request.Action}' is not a bulk action this studio knows"),
            };

            await Task.WhenAll(pending);
            affected += pending.Count(task => task.Result);
        }

        return affected;
    }
}
