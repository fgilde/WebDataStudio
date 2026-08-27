using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// A folder as an object store. It keeps the same contract as the buckets, and one promise of its
/// own: a connection that hands somebody a folder must not hand them the disk.
public class LocalObjectStoreTests : ObjectStoreContract, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wds-local-store").FullName;

    protected override IObjectStore Store => _store ??=
        new LocalObjectStore(StorageUrl.Parse(new Uri(_root).AbsoluteUri));

    private IObjectStore? _store;

    public void Dispose() => TestDirectory.Remove(_root);

    [Fact]
    public async Task A_key_that_climbs_out_of_the_folder_is_refused()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Store.HeadAsync("../../etc/passwd", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writing_creates_the_folders_on_the_way()
    {
        await PutAsync("deep/deeper/still/file.txt", "here");

        Assert.NotNull(await Store.HeadAsync("deep/deeper/still/file.txt",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void It_needs_no_duckdb_secret() => Assert.Null(Store.SecretStatement());
}
