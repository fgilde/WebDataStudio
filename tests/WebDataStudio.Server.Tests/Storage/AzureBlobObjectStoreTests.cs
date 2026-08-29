using Testcontainers.Azurite;
using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// Azure Blob against Azurite, which is Azure Blob on a laptop.
public class AzureBlobObjectStoreTests : ObjectStoreContract, IAsyncLifetime
{
    // The SDK speaks a newer service version than the emulator knows, and Azurite's own answer to
    // that is this switch. Pinning the SDK down instead would make every real deployment speak an
    // older Azure to please a test.
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithCommand("--skipApiVersionCheck").Build();
    private AzureBlobObjectStore? _store;

    protected override IObjectStore Store => _store!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Azurite's well-known development account, which is the whole point of it.
        var url = "azblob://devstoreaccount1/lake?connectionstring="
                + Uri.EscapeDataString(_container.GetConnectionString());

        var target = StorageUrl.Parse(url);

        var service = new Azure.Storage.Blobs.BlobServiceClient(_container.GetConnectionString());
        await service.GetBlobContainerClient("lake")
            .CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);

        _store = new AzureBlobObjectStore(target);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task A_container_that_was_never_created_is_one_sentence()
    {
        // An app host that declared the account and not the container, or a name with a typo. The
        // provider answers with a page of XML about it; nobody should have to read that to learn
        // that the container is not there.
        var missing = new AzureBlobObjectStore(StorageUrl.Parse(
            "azblob://devstoreaccount1/not-created?connectionstring="
            + Uri.EscapeDataString(_container.GetConnectionString())));

        var failure = await Assert.ThrowsAsync<StorageContainerMissingException>(
            () => missing.ListAsync("", null, 10, TestContext.Current.CancellationToken));

        Assert.Equal("not-created", failure.Container);
        Assert.Contains("no container called 'not-created'", failure.Message);
        Assert.DoesNotContain("<?xml", failure.Message);
    }

    [Fact]
    public async Task A_key_that_is_not_there_is_still_an_ordinary_nothing()
    {
        // The container exists and the file does not, which is not the same question.
        Assert.Null(await Store.HeadAsync("nothing/here.csv", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_uri_is_the_one_duckdb_reads() =>
        Assert.Equal("az://lake/exports/2026/a.csv", Store.SqlUri("exports/2026/a.csv"));

    [Fact]
    public void A_connection_string_becomes_the_secret()
    {
        var secret = Store.SecretStatement();

        Assert.Contains("TYPE azure", secret);
        Assert.Contains("CONNECTION_STRING", secret);
    }

    [Fact]
    public void An_account_key_becomes_a_connection_string_for_duckdb()
    {
        // Base64, because that is what an account key is and the SDK checks.
        const string key = "YS10ZXN0LWFjY291bnQta2V5";
        var store = new AzureBlobObjectStore(StorageUrl.Parse($"azblob://mystorage/lake?key={key}"));

        Assert.Contains("AccountName=mystorage", store.SecretStatement());
        Assert.Contains($"AccountKey={key}", store.SecretStatement());
    }

    [Fact]
    public void Without_a_key_duckdb_walks_the_same_credential_chain()
    {
        var store = new AzureBlobObjectStore(StorageUrl.Parse("azblob://mystorage/lake"));

        // The managed identity in a deployment, and whatever a developer is signed in as locally.
        Assert.Contains("PROVIDER credential_chain", store.SecretStatement());
        Assert.Contains("ACCOUNT_NAME 'mystorage'", store.SecretStatement());
    }

    [Fact]
    public async Task A_prefix_scoped_connection_only_sees_its_own_folder()
    {
        await SeedAsync();

        var scoped = new AzureBlobObjectStore(StorageUrl.Parse(
            "azblob://devstoreaccount1/lake/exports/2026?connectionstring="
            + Uri.EscapeDataString(_container.GetConnectionString())));

        var page = await scoped.ListAsync("", null, 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Entries.Count);
        Assert.DoesNotContain(page.Entries, entry => entry.Name == "root.txt");
    }

    [Fact]
    public void A_service_uri_means_the_identity_this_process_runs_as()
    {
        // What a deployed Aspire blob resource hands over. There is no key in it, and there should
        // not be one: DuckDB walks the same credential chain the SDK does.
        var store = new AzureBlobObjectStore(StorageUrl.Parse(
            "azblob:///exports?connectionstring=https://acct.blob.core.windows.net/"));

        var secret = store.SecretStatement();

        Assert.Contains("PROVIDER credential_chain", secret);
        Assert.Contains("ACCOUNT_NAME 'acct'", secret);
        Assert.DoesNotContain("CONNECTION_STRING", secret);
    }
}
