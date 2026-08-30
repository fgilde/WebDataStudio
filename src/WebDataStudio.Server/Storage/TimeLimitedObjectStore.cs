namespace WebDataStudio.Server.Storage;

/// The bucket did not answer in time.
///
/// A server that refuses a connection is an error in a second; a server that is simply *gone* — a
/// container somebody stopped, a machine off the network, an endpoint that was a host port until it
/// changed — accepts nothing and refuses nothing, and the request waits. Every provider's client has
/// its own idea of how long to keep trying, and all of them are longer than anybody watching a
/// spinner is willing to wait for.
public sealed class StorageUnreachableException(string what, TimeSpan after, Exception? inner = null)
    : Exception($"{what} did not answer within {after.TotalSeconds:0} seconds. The endpoint may be "
                + "gone, or reachable only from somewhere else than this studio.", inner);

/// Gives every call to a bucket a deadline, so "no answer" becomes a sentence rather than a spinner.
///
/// One decorator rather than a timeout inside each provider's client: they configure this in three
/// different places and none of them covers a stream that has already started, and a reader that
/// stalls mid-file is the same problem as a listing that never returns.
public sealed class TimeLimitedObjectStore(IObjectStore inner, TimeSpan limit) : IObjectStore
{
    /// Long enough for a large listing over a slow link, short enough that nobody wonders whether
    /// the studio is still alive.
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(20);

    public StorageTarget Target => inner.Target;

    public string SqlUri(string key) => inner.SqlUri(key);
    public string? SecretStatement() => inner.SecretStatement();

    public Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct) =>
        Within("listing this bucket", token => inner.ListAsync(prefix, cursor, max, token), ct);

    public Task<StorageObject?> HeadAsync(string key, CancellationToken ct) =>
        Within($"reading '{key}'", token => inner.HeadAsync(key, token), ct);

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
        Within($"opening '{key}'", token => inner.OpenReadAsync(key, token), ct);

    public Task WriteAsync(string key, Stream content, string? contentType, CancellationToken ct) =>
        Within($"writing '{key}'", async token =>
        {
            await inner.WriteAsync(key, content, contentType, token);
            return true;
        }, ct);

    public Task DeleteAsync(string key, CancellationToken ct) =>
        Within($"deleting '{key}'", async token =>
        {
            await inner.DeleteAsync(key, token);
            return true;
        }, ct);

    private async Task<T> Within<T>(string what, Func<CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);

        try
        {
            return await work(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Ours, not the caller's: the browser is still waiting and deserves the reason.
            throw new StorageUnreachableException(what, limit);
        }
        catch (Exception e) when (Timeout(e) && !ct.IsCancellationRequested)
        {
            // Some clients answer a deadline with their own kind of exception rather than by
            // honouring the token.
            throw new StorageUnreachableException(what, limit, e);
        }
    }

    /// Whether this is a client giving up on a network that never answered, at whatever depth its
    /// own retries wrapped it.
    private static bool Timeout(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or TaskCanceledException) return true;
            if (current is HttpRequestException http
                && http.HttpRequestError is HttpRequestError.ConnectionError) return true;
        }

        return false;
    }
}
