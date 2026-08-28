using StackExchange.Redis;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.Redis;

/// A page of rows out of a key space.
///
/// A Redis key was "one value, not a page of rows", and the data tab said so and stopped. But a key
/// space is a table if you look at it right — key, type, expiry, size — and so is a key's own
/// contents: a hash is field and value, a sorted set is member and score, a list is index and value.
/// Both are worth having in the grid, because that is where the filter language, the sorting and the
/// export already live.
public static class RedisPage
{
    /// How many keys one page may look at. A key space is scanned, not indexed, so the answer is
    /// "this many, in the order the server hands them over" rather than a promise about all of them.
    public const int MaxScan = 20_000;

    public static readonly ColumnMeta[] KeyColumns =
    [
        new("key", "string", false),
        new("type", "string", false),
        new("ttl", "seconds", true),
        new("length", "number", true),
        new("memory", "bytes", true),
    ];

    /// The keys under a prefix, with what each one is. Sorted and filtered here rather than by the
    /// server: Redis has no ORDER BY, and a page of twenty thousand keys is small enough to order in
    /// memory.
    public static async Task<TabularPage> KeysAsync(IDatabase db, IServer server, int database,
        string prefix, PageQuery query, CancellationToken ct)
    {
        var pattern = prefix.Length == 0 ? "*" : prefix + "*";
        var keys = new List<string>();

        foreach (var key in server.Keys(database, pattern, pageSize: 500))
        {
            ct.ThrowIfCancellationRequested();

            keys.Add(key.ToString());
            if (keys.Count >= MaxScan) break;
        }

        var truncated = keys.Count >= MaxScan;

        // The filter is applied on the key name before anything is asked about each key: asking the
        // server about twenty thousand keys to show two hundred would be the expensive way round.
        if (query.FilterColumn is "key" && query.Filter is { Length: > 0 } text)
            keys = keys.Where(key => Matches(key, text)).ToList();

        keys.Sort(StringComparer.Ordinal);

        var total = keys.Count;
        var page = keys.Skip(query.Offset).Take(query.Limit).ToList();
        var rows = new List<object?[]>(page.Count);

        foreach (var key in page)
        {
            ct.ThrowIfCancellationRequested();

            var type = await db.KeyTypeAsync(key);
            var ttl = await db.KeyTimeToLiveAsync(key);

            rows.Add([
                key,
                type.ToString().ToLowerInvariant(),
                ttl is null ? null : (long)ttl.Value.TotalSeconds,
                await LengthAsync(db, key, type),
                await MemoryAsync(db, key),
            ]);
        }

        // Sorting on anything but the key means sorting the page that was read, which is honest as
        // long as it says so.
        var note = truncated
            ? $"the first {MaxScan} keys of this key space"
            : null;

        if (query.Sort is { Length: > 0 } sort && sort != "key")
        {
            rows = Order(rows, sort, query.Desc);
            note = Join(note, $"sorted by {sort} within this page: Redis has no order of its own");
        }
        else if (query.Sort == "key" && query.Desc)
        {
            rows.Reverse();
        }

        if (query.FilterColumn is { Length: > 0 } and not "key")
            note = Join(note, $"filtering on {query.FilterColumn} is not something a key space can do");

        return new TabularPage(KeyColumns, rows, total,
            Editable: false,
            Reason: "a key is written with a Redis command: open one, or use a query tab",
            Note: note);
    }

    /// One key's contents, as the table its type makes: field and value, member and score, index and
    /// value, or the string itself.
    public static async Task<TabularPage> ValueAsync(IDatabase db, string key, PageQuery query,
        CancellationToken ct)
    {
        var type = await db.KeyTypeAsync(key);
        ct.ThrowIfCancellationRequested();

        switch (type)
        {
            case RedisType.Hash:
            {
                var entries = await db.HashGetAllAsync(key);
                var rows = entries
                    .Select(entry => new object?[] { entry.Name.ToString(), entry.Value.ToString() })
                    .ToList();

                return Page([new("field", "string", false), new("value", "string", true)], rows, query,
                    "a hash field is written with HSET");
            }

            case RedisType.List:
            {
                var values = await db.ListRangeAsync(key);
                var rows = values
                    .Select((value, index) => new object?[] { (long)index, value.ToString() })
                    .ToList();

                return Page([new("index", "number", false), new("value", "string", true)], rows, query,
                    "a list is written with LSET, LPUSH or RPUSH");
            }

            case RedisType.Set:
            {
                var members = await db.SetMembersAsync(key);
                var rows = members.Select(member => new object?[] { member.ToString() }).ToList();

                return Page([new("member", "string", false)], rows, query,
                    "a set is written with SADD and SREM");
            }

            case RedisType.SortedSet:
            {
                var entries = await db.SortedSetRangeByRankWithScoresAsync(key);
                var rows = entries
                    .Select(entry => new object?[] { entry.Element.ToString(), entry.Score })
                    .ToList();

                return Page([new("member", "string", false), new("score", "number", true)], rows, query,
                    "a sorted set is written with ZADD");
            }

            case RedisType.Stream:
            {
                var entries = await db.StreamRangeAsync(key, count: query.Offset + query.Limit);
                var rows = entries
                    .Select(entry => new object?[]
                    {
                        entry.Id.ToString(),
                        string.Join(", ", entry.Values.Select(value => $"{value.Name}={value.Value}")),
                    })
                    .ToList();

                return Page([new("id", "string", false), new("fields", "string", true)], rows, query,
                    "a stream is written with XADD");
            }

            case RedisType.String:
            {
                var value = await db.StringGetAsync(key);

                return Page([new("value", "string", true), new("length", "number", true)],
                    [[value.ToString(), (long)(value.HasValue ? value.ToString().Length : 0)]],
                    query, "a string is written with SET");
            }

            default:
                return new TabularPage([new("key", "string", false)], [[key]], 1, false,
                    $"a {type.ToString().ToLowerInvariant()} key has no rows the studio can show", null);
        }
    }

    /// The rows this page asked for, out of the ones the key held. Redis hands over the whole key,
    /// which is what its own commands do as well — the paging here is the grid's, not the server's.
    private static TabularPage Page(ColumnMeta[] columns, List<object?[]> rows, PageQuery query,
        string reason)
    {
        var total = rows.Count;
        string? note = null;

        if (query.FilterColumn is { Length: > 0 } column && query.Filter is { Length: > 0 } text)
        {
            var index = Array.FindIndex(columns, candidate => candidate.Name == column);

            if (index >= 0)
                rows = rows.Where(row => Matches(row[index]?.ToString() ?? "", text)).ToList();
            else
                note = $"there is no {column} in a key of this type";

            total = rows.Count;
        }

        if (query.Sort is { Length: > 0 } sort)
        {
            var index = Array.FindIndex(columns, candidate => candidate.Name == sort);
            if (index >= 0) rows = Order(rows, index, query.Desc);
        }

        return new TabularPage(columns, rows.Skip(query.Offset).Take(query.Limit).ToList(), total,
            Editable: false, Reason: reason, Note: note);
    }

    private static List<object?[]> Order(List<object?[]> rows, string sort, bool desc) =>
        Order(rows, Array.FindIndex(KeyColumns, column => column.Name == sort) is var index && index >= 0
            ? index
            : 0, desc);

    private static List<object?[]> Order(List<object?[]> rows, int index, bool desc)
    {
        // Numbers as numbers, everything else as text: a ttl of 100 sorts above one of 99.
        var ordered = rows
            .OrderBy(row => row[index] is null)
            .ThenBy(row => row[index] as IComparable ?? row[index]?.ToString(),
                Comparer<object?>.Create(Compare))
            .ToList();

        if (desc) ordered.Reverse();
        return ordered;
    }

    private static int Compare(object? left, object? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        if (left is IComparable comparable && left.GetType() == right.GetType())
            return comparable.CompareTo(right);

        return StringComparer.Ordinal.Compare(left.ToString(), right.ToString());
    }

    /// The studio's filter language, as much of it as a key name can answer: `^starts`, `$ends`,
    /// `~hasn't`, and a plain word for "contains".
    public static bool Matches(string value, string filter)
    {
        var text = filter.Trim();
        if (text.Length == 0) return true;

        if (text.StartsWith('^'))
            return value.StartsWith(text[1..], StringComparison.OrdinalIgnoreCase);

        if (text.StartsWith('$'))
            return value.EndsWith(text[1..], StringComparison.OrdinalIgnoreCase);

        if (text.StartsWith('~'))
            return !value.Contains(text[1..], StringComparison.OrdinalIgnoreCase);

        if (text.StartsWith('='))
            return value.Equals(text[1..], StringComparison.OrdinalIgnoreCase);

        if (text.StartsWith('+')) text = text[1..];

        return value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long?> LengthAsync(IDatabase db, string key, RedisType type) => type switch
    {
        RedisType.String => (long?)await db.StringLengthAsync(key),
        RedisType.Hash => await db.HashLengthAsync(key),
        RedisType.List => await db.ListLengthAsync(key),
        RedisType.Set => await db.SetLengthAsync(key),
        RedisType.SortedSet => await db.SortedSetLengthAsync(key),
        RedisType.Stream => await db.StreamLengthAsync(key),
        _ => null,
    };

    /// What the key costs in memory, where the server will say. `MEMORY USAGE` is not available on
    /// every deployment — a managed Redis with the command disabled answers with an error, and an
    /// empty column is better than a failed page.
    private static async Task<long?> MemoryAsync(IDatabase db, string key)
    {
        try
        {
            var result = await db.ExecuteAsync("MEMORY", "USAGE", key);
            return result.IsNull ? null : (long)result;
        }
        catch (RedisException)
        {
            return null;
        }
    }

    private static string Join(string? first, string second) =>
        first is { Length: > 0 } ? $"{first}; {second}" : second;
}
