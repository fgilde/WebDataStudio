using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace WebDataStudio.Server.Drivers.Redis;

/// One key's value, shaped by its type: a string comes back as a string, a hash as an object, a
/// list as an array, a sorted set as members with scores, a stream as entries. The browser renders
/// each of those differently, and flattening them into one shape is what makes a Redis client feel
/// like a spreadsheet with the wrong columns.
public sealed record ValueDto(
    string Key, string Type, long? TtlSeconds, JsonElement Value, long Length, string? Encoding);

/// An edit, spelled as the Redis command it will become. `Payload` carries whatever the operation
/// needs — a value, a field and a value, a member and a score.
public sealed record ValueEditRequest(
    int Database, string Key, string Operation, JsonElement Payload);

/// The commands an edit would run, for the preview the studio shows before anything happens.
public sealed record ValuePreviewDto(string Hash, IReadOnlyList<string> Commands, bool Destructive);

/// Reading and writing a single key. Every write goes through Plan and then Apply: the studio never
/// mutates on the first call, and Redis has no transaction to roll back afterwards, which makes the
/// preview the only place a mistake can still be caught.
public static class RedisValues
{
    /// Entries fetched for a collection in one go. A hash with a million fields is real, and the
    /// browser pages through it rather than asking for all of it.
    public const int PageSize = 500;

    public static async Task<ValueDto?> ReadAsync(
        IDatabase db, string key, long offset, int count, CancellationToken ct)
    {
        var type = await db.KeyTypeAsync(key);
        if (type == RedisType.None) return null;

        var ttl = await db.KeyTimeToLiveAsync(key);
        var length = await RedisKeyspace.LengthAsync(db, key, type) ?? 0;
        var take = Math.Clamp(count, 1, PageSize);
        ct.ThrowIfCancellationRequested();

        var value = type switch
        {
            RedisType.String => Json((string?)await db.StringGetAsync(key)),
            RedisType.Hash => JsonObject(await db.HashGetAllAsync(key)),
            RedisType.List => JsonArray(await db.ListRangeAsync(key, offset, offset + take - 1)),
            // A set has no order, so paging it means scanning: SMEMBERS on a large set is the
            // classic way to stall a Redis.
            RedisType.Set => JsonArray(await ScanSetAsync(db, key, offset, take)),
            RedisType.SortedSet => JsonScored(
                await db.SortedSetRangeByRankWithScoresAsync(key, offset, offset + take - 1)),
            RedisType.Stream => JsonStream(await db.StreamRangeAsync(key, count: take)),
            _ => Json(null),
        };

        // The encoding is what tells a small hash from a hashtable and a listpack from a quicklist;
        // it is the first thing anybody investigating memory asks for.
        var encoding = await db.ExecuteAsync("OBJECT", "ENCODING", key);

        return new ValueDto(key, type.ToString().ToLowerInvariant(),
            ttl is null ? null : (long)ttl.Value.TotalSeconds,
            value, length, encoding.IsNull ? null : (string?)encoding);
    }

    /// The commands an edit becomes, without running any of them. The list is what the studio shows
    /// and what Apply later executes, so the two can never drift apart.
    public static IReadOnlyList<string> Plan(ValueEditRequest edit)
    {
        var key = edit.Key;
        var payload = edit.Payload;

        string Text(string name) => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var found)
            ? found.ValueKind == JsonValueKind.String ? found.GetString() ?? "" : found.ToString()
            : "";

        return edit.Operation.ToLowerInvariant() switch
        {
            "set" => [$"SET {Quote(key)} {Quote(Text("value"))}"],
            "append" => [$"APPEND {Quote(key)} {Quote(Text("value"))}"],
            "hset" => [$"HSET {Quote(key)} {Quote(Text("field"))} {Quote(Text("value"))}"],
            "hdel" => [$"HDEL {Quote(key)} {Quote(Text("field"))}"],
            "rpush" => [$"RPUSH {Quote(key)} {Quote(Text("value"))}"],
            "lpush" => [$"LPUSH {Quote(key)} {Quote(Text("value"))}"],
            "lset" => [$"LSET {Quote(key)} {Text("index")} {Quote(Text("value"))}"],
            // Removing by value rather than by index is Redis's own model; the browser sends the
            // value it showed, so a shifted list cannot delete the wrong entry.
            "lrem" => [$"LREM {Quote(key)} 1 {Quote(Text("value"))}"],
            "sadd" => [$"SADD {Quote(key)} {Quote(Text("value"))}"],
            "srem" => [$"SREM {Quote(key)} {Quote(Text("value"))}"],
            "zadd" => [$"ZADD {Quote(key)} {Text("score")} {Quote(Text("member"))}"],
            "zrem" => [$"ZREM {Quote(key)} {Quote(Text("member"))}"],
            "xadd" => [$"XADD {Quote(key)} * {Fields(payload)}"],
            "expire" => [$"EXPIRE {Quote(key)} {Text("seconds")}"],
            "persist" => [$"PERSIST {Quote(key)}"],
            "rename" => [$"RENAME {Quote(key)} {Quote(Text("newKey"))}"],
            "del" => [$"DEL {Quote(key)}"],
            _ => throw new ArgumentException($"'{edit.Operation}' is not an operation this studio knows"),
        };
    }

    /// True for anything that removes data. The studio says so before it happens.
    public static bool IsDestructive(string operation) =>
        operation.ToLowerInvariant() is "del" or "hdel" or "lrem" or "srem" or "zrem" or "set"
            or "rename";

    public static async Task<int> ApplyAsync(
        IDatabase db, int database, IReadOnlyList<string> commands, CancellationToken ct)
    {
        var executed = 0;

        foreach (var command in commands)
        {
            ct.ThrowIfCancellationRequested();

            var parts = SplitCommand(command);
            if (parts.Count == 0) continue;

            await db.ExecuteAsync(parts[0], parts.Skip(1).Cast<object>().ToArray());
            executed++;
        }

        _ = database;
        return executed;
    }

    /// A stable hash of the planned commands: Apply executes what was approved, not what a second
    /// request happens to ask for.
    public static string HashOf(IReadOnlyList<string> commands) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", commands))))
            .ToLowerInvariant();

    private static string Fields(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "";

        var parts = new List<string>();
        foreach (var property in payload.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? ""
                : property.Value.ToString();
            parts.Add($"{Quote(property.Name)} {Quote(value)}");
        }

        return string.Join(" ", parts);
    }

    /// Quotes the way redis-cli does, so the preview is a command somebody can paste into a shell.
    private static string Quote(string value) =>
        value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'')
            ? value
            : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// The inverse of Quote: the preview text is the source of truth for what Apply runs.
    internal static List<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;

        foreach (var character in command)
        {
            if (escaped) { current.Append(character); escaped = false; continue; }
            if (character == '\\' && quoted) { escaped = true; continue; }
            if (character == '"') { quoted = !quoted; continue; }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    private static async Task<RedisValue[]> ScanSetAsync(IDatabase db, string key, long offset, int take)
    {
        var members = new List<RedisValue>(take);
        var skipped = 0L;

        await foreach (var member in db.SetScanAsync(key, pageSize: PageSize))
        {
            if (skipped++ < offset) continue;

            members.Add(member);
            if (members.Count >= take) break;
        }

        return [.. members];
    }

    private static JsonElement Json(string? value) =>
        JsonSerializer.SerializeToElement(value);

    private static JsonElement JsonObject(HashEntry[] entries) =>
        JsonSerializer.SerializeToElement(
            entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString()));

    private static JsonElement JsonArray(RedisValue[] values) =>
        JsonSerializer.SerializeToElement(values.Select(v => v.ToString()).ToArray());

    private static JsonElement JsonScored(SortedSetEntry[] entries) =>
        JsonSerializer.SerializeToElement(
            entries.Select(e => new { member = e.Element.ToString(), score = e.Score }).ToArray());

    private static JsonElement JsonStream(StreamEntry[] entries) =>
        JsonSerializer.SerializeToElement(entries.Select(entry => new
        {
            id = entry.Id.ToString(),
            values = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString()),
        }).ToArray());
}
