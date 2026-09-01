using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// A bucket that stops answering must become a sentence, not a spinner.
public class TimeLimitedObjectStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly StorageTarget Target = StorageUrl.Parse("s3://lake?region=eu-central-1");

    /// A store that never answers, which is what a container somebody stopped looks like: it
    /// accepts nothing and refuses nothing.
    private sealed class Silent : IObjectStore
    {
        public StorageTarget Target => TimeLimitedObjectStoreTests.Target;

        public async Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new StoragePage([], null);
        }

        public async Task<StorageObject?> HeadAsync(string key, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }

        public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Stream.Null;
        }

        public Task WriteAsync(string key, Stream content, string? contentType, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);

        public Task DeleteAsync(string key, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

        public string SqlUri(string key) => $"s3://lake/{key}";
        public string? SecretStatement() => null;
    }

    /// One that answers with the client's own idea of "gave up", rather than by honouring the token.
    private sealed class Stubborn(Exception failure) : IObjectStore
    {
        public StorageTarget Target => TimeLimitedObjectStoreTests.Target;

        public Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct) =>
            Task.FromException<StoragePage>(failure);

        public Task<StorageObject?> HeadAsync(string key, CancellationToken ct) =>
            Task.FromException<StorageObject?>(failure);

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
            Task.FromException<Stream>(failure);

        public Task WriteAsync(string key, Stream content, string? contentType, CancellationToken ct) =>
            Task.FromException(failure);

        public Task DeleteAsync(string key, CancellationToken ct) => Task.FromException(failure);

        public string SqlUri(string key) => $"s3://lake/{key}";
        public string? SecretStatement() => null;
    }

    private static TimeLimitedObjectStore Quick(IObjectStore inner) =>
        new(inner, TimeSpan.FromMilliseconds(120));

    [Fact]
    public async Task A_listing_that_never_comes_back_becomes_a_sentence()
    {
        var failure = await Assert.ThrowsAsync<StorageUnreachableException>(
            () => Quick(new Silent()).ListAsync("", null, 10, Ct));

        Assert.Contains("did not answer", failure.Message);
        Assert.Contains("listing this bucket", failure.Message);
    }

    [Fact]
    public async Task So_does_every_other_thing_a_bucket_is_asked()
    {
        var store = Quick(new Silent());

        await Assert.ThrowsAsync<StorageUnreachableException>(() => store.HeadAsync("a.csv", Ct));
        await Assert.ThrowsAsync<StorageUnreachableException>(() => store.OpenReadAsync("a.csv", Ct));
        await Assert.ThrowsAsync<StorageUnreachableException>(
            () => store.WriteAsync("a.csv", Stream.Null, "text/csv", Ct));
        await Assert.ThrowsAsync<StorageUnreachableException>(() => store.DeleteAsync("a.csv", Ct));
    }

    [Fact]
    public async Task The_name_of_the_thing_that_stalled_is_in_the_message()
    {
        var failure = await Assert.ThrowsAsync<StorageUnreachableException>(
            () => Quick(new Silent()).HeadAsync("exports/people.csv", Ct));

        Assert.Contains("exports/people.csv", failure.Message);
    }

    [Fact]
    public async Task A_client_that_gives_up_its_own_way_is_the_same_answer()
    {
        // Some of them answer a deadline with their own exception instead of the token.
        foreach (var failure in new Exception[]
        {
            new TimeoutException("the request timed out"),
            new HttpRequestException(HttpRequestError.ConnectionError, "cannot connect"),
            new InvalidOperationException("wrapped", new TimeoutException("underneath")),
        })
        {
            await Assert.ThrowsAsync<StorageUnreachableException>(
                () => Quick(new Stubborn(failure)).ListAsync("", null, 10, Ct));
        }
    }

    [Fact]
    public async Task Anything_else_is_left_exactly_as_it_was()
    {
        // A missing container is a different sentence, and it must survive this wrapper unchanged.
        var missing = new StorageContainerMissingException("lake");

        var failure = await Assert.ThrowsAsync<StorageContainerMissingException>(
            () => Quick(new Stubborn(missing)).ListAsync("", null, 10, Ct));

        Assert.Equal("lake", failure.Container);
    }

    [Fact]
    public async Task The_caller_giving_up_stays_the_caller_giving_up()
    {
        // A browser that closed the tab is not a bucket that stopped answering.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Quick(new Silent()).ListAsync("", null, 10, cancelled.Token));
    }

    [Fact]
    public void A_folder_on_this_machine_is_not_wrapped()
    {
        // It answers or it does not; there is no network to stall on, and a large local listing
        // should not be cut off at twenty seconds.
        Assert.IsType<LocalObjectStore>(
            ObjectStores.For(StorageUrl.Parse(new Uri(Path.GetTempPath()).AbsoluteUri)));
    }

    [Fact]
    public void Everything_reached_over_a_network_is()
    {
        Assert.IsType<TimeLimitedObjectStore>(
            ObjectStores.For(StorageUrl.Parse("s3://lake?region=eu-central-1")));
    }
}
