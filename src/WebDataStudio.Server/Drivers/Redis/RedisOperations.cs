using StackExchange.Redis;

namespace WebDataStudio.Server.Drivers.Redis;

public sealed record ConsumerGroupDto(string Name, long Consumers, long Pending, string LastDelivered);
public sealed record PendingEntryDto(string Id, string Consumer, long IdleMs, long DeliveryCount);

/// A stream and the groups reading it. Consumer groups are the reason streams exist, and a client
/// that shows only the entries cannot answer the question people actually have: what is stuck.
public sealed record StreamInfoDto(
    long Length, string? FirstId, string? LastId,
    IReadOnlyList<ConsumerGroupDto> Groups, IReadOnlyList<PendingEntryDto> Pending);

public sealed record SlowEntryDto(long Id, DateTimeOffset At, long MicroSeconds, string Command, string? Client);

public sealed record ClientDto(string Id, string? Address, string? Name, long IdleSeconds, string? LastCommand);

/// The administrative half of a Redis client: streams with their consumer groups, the slow log, and
/// who is connected. All read-only, all one command each.
public static class RedisOperations
{
    public static async Task<StreamInfoDto> StreamAsync(IDatabase db, string key, CancellationToken ct)
    {
        var info = await db.StreamInfoAsync(key);
        ct.ThrowIfCancellationRequested();

        var groups = new List<ConsumerGroupDto>();
        var pending = new List<PendingEntryDto>();

        foreach (var group in await db.StreamGroupInfoAsync(key))
        {
            groups.Add(new ConsumerGroupDto(
                group.Name, group.ConsumerCount, group.PendingMessageCount, group.LastDeliveredId));

            // The pending list is what "stuck" looks like: delivered, not acknowledged, and by whom.
            foreach (var entry in await db.StreamPendingMessagesAsync(key, group.Name, 20, RedisValue.Null))
                pending.Add(new PendingEntryDto(
                    entry.MessageId, entry.ConsumerName, entry.IdleTimeInMilliseconds,
                    entry.DeliveryCount));
        }

        return new StreamInfoDto(
            info.Length,
            info.FirstEntry.IsNull ? null : info.FirstEntry.Id.ToString(),
            info.LastEntry.IsNull ? null : info.LastEntry.Id.ToString(),
            groups, pending);
    }

    /// SLOWLOG GET. The threshold is the server's own (`slowlog-log-slower-than`), so an empty list
    /// means "nothing was slow", not "not supported".
    public static async Task<IReadOnlyList<SlowEntryDto>> SlowLogAsync(
        IServer server, int count, CancellationToken ct)
    {
        var entries = await server.SlowlogGetAsync(count);
        ct.ThrowIfCancellationRequested();

        return [.. entries.Select(entry => new SlowEntryDto(
            entry.UniqueId,
            entry.Time,
            (long)entry.Duration.TotalMicroseconds,
            string.Join(" ", entry.Arguments.Select(argument => argument.ToString())),
            // The client is not part of SLOWLOG's reply in every server version; the trace only
            // carries what the server sent.
            null))];
    }

    public static async Task<IReadOnlyList<ClientDto>> ClientsAsync(IServer server, CancellationToken ct)
    {
        var clients = await server.ClientListAsync();
        ct.ThrowIfCancellationRequested();

        return [.. clients.Select(client => new ClientDto(
            client.Id.ToString(),
            client.Address?.ToString(),
            string.IsNullOrWhiteSpace(client.Name) ? null : client.Name,
            client.AgeSeconds,
            client.LastCommand))];
    }
}
