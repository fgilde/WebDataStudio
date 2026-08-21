using System.Security.Cryptography;
using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Mcp;

namespace WebDataStudio.Server.Services;

/// A result somebody kept: the rows as they were, not a query to run again.
public sealed record SharedResult(
    string Id, string ConnectionName, string Sql, string? By, DateTimeOffset At,
    DateTimeOffset ExpiresAt, IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows,
    bool Truncated);

/// Whether results can be shared at all, and with whom. Off by default: a link that hands rows to
/// anybody who has it is a decision, not a default.
public sealed record ShareOptions(bool Enabled, TimeSpan Ttl, bool Public, int MaxRows)
{
    public static ShareOptions FromConfiguration(IConfiguration config)
    {
        var enabled = string.Equals(config["WDS_SHARE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);

        var hours = int.TryParse(config["WDS_SHARE_TTL_HOURS"], out var value) && value > 0 ? value : 168;
        var open = string.Equals(config["WDS_SHARE_PUBLIC"], "true", StringComparison.OrdinalIgnoreCase);
        var rows = int.TryParse(config["WDS_SHARE_MAX_ROWS"], out var cap) && cap > 0 ? cap : 1_000;

        return new ShareOptions(enabled, TimeSpan.FromHours(hours), open, Math.Min(rows, 10_000));
    }
}

/// Snapshots a result and hands out a link to it — the answer to "here is what I am seeing" that is
/// not a screenshot.
///
/// A snapshot, not a saved query: the link shows the rows as they were, so it cannot run anything
/// later and cannot show more than the person who made it could see. Masking applies before the rows
/// are stored, so a masked column is masked in the link for good.
public sealed class ResultShares(
    ShareOptions options, WorkspaceStore workspace, ConnectionRegistry registry,
    SessionFactory factory, MaskPolicyStore policies)
{
    private const string Prefix = "share:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => options.Enabled;

    public bool Public => options.Public;

    /// Runs the statement and keeps its rows. The statement has to read: a link is a record of
    /// something, not a button that changes it.
    public async Task<SharedResult> CreateAsync(
        string connectionId, string sql, string? by, CancellationToken ct)
    {
        if (!options.Enabled)
            throw new InvalidOperationException(
                "sharing is off; set WDS_SHARE_ENABLED=true to allow it");

        if (!ReadOnlyStatement.Looks(sql))
            throw new InvalidOperationException(
                "only a reading statement can be shared: a link is a record, not a button");

        var spec = registry.Find(connectionId)
            ?? throw new UnknownConnectionException(connectionId);

        var (driver, session) = await factory.OpenAsync(connectionId, ct);
        await using (session)
        {
            var columns = new List<ColumnMeta>();
            var rows = new List<string?[]>();
            string? error = null;
            var truncated = false;

            var request = new ScriptRequest(sql, options.MaxRows + 1, 120);

            await foreach (var chunk in Masking.Stream(
                driver.ExecuteAsync(session, request, ct), policies.For(connectionId), ct))
                switch (chunk)
                {
                    case ResultChunk.Columns page: columns = [.. page.Items]; break;
                    case ResultChunk.Error failure: error = failure.Text; break;
                    case ResultChunk.Rows page:
                        foreach (var row in page.Items)
                        {
                            if (rows.Count >= options.MaxRows) { truncated = true; break; }
                            rows.Add([.. row.Select(Text)]);
                        }
                        break;
                }

            if (error is not null) throw new InvalidOperationException(error);

            var now = DateTimeOffset.UtcNow;
            var shared = new SharedResult(
                NewId(), spec.Name, sql, by, now, now + options.Ttl,
                [.. columns.Select(column => column.Name)], rows, truncated);

            workspace.SaveItem($"{Prefix}{shared.Id}", JsonSerializer.Serialize(shared, Json));
            return shared;
        }
    }

    /// The snapshot, or null when there is none — or when it has expired, which is the same thing
    /// as far as the caller is concerned.
    public SharedResult? Find(string id)
    {
        if (!options.Enabled || !Safe(id)) return null;

        try
        {
            var json = workspace.LoadItem($"{Prefix}{id}");
            if (json is null) return null;

            var shared = JsonSerializer.Deserialize<SharedResult>(json, Json);
            if (shared is null) return null;

            return shared.ExpiresAt > DateTimeOffset.UtcNow ? shared : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// 128 bits from a cryptographic source: a link that anybody with it can open must not be a
    /// link anybody can guess.
    private static string NewId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// Ids are hex, and the id becomes part of a workspace key — so anything else is refused
    /// rather than looked up.
    private static bool Safe(string id) =>
        id.Length is > 8 and <= 64 && id.All(Uri.IsHexDigit);

    private static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime date => date.ToString("O"),
        DateTimeOffset date => date.ToString("O"),
        _ => value.ToString(),
    };
}
