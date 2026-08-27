using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// The URL a storage connection is configured with. One engine id, four schemes, credentials
/// optional — because a deployment that has to carry a key for its own storage account is carrying a
/// secret it did not need.
public class StorageUrlTests
{
    [Theory]
    [InlineData("s3://bucket")]
    [InlineData("azblob://account/container")]
    [InlineData("gs://bucket")]
    [InlineData("file:///data/incoming")]
    public void The_four_schemes_are_storage(string url)
    {
        Assert.True(StorageUrl.IsStorageScheme(new Uri(url).Scheme));
        Assert.NotNull(StorageUrl.Parse(url));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("redis")]
    [InlineData("http")]
    public void Everything_else_is_not(string scheme) =>
        Assert.False(StorageUrl.IsStorageScheme(scheme));

    [Fact]
    public void An_s3_url_is_a_bucket_and_a_prefix()
    {
        var target = StorageUrl.Parse("s3://lake/exports/2026?region=eu-central-1");

        Assert.Equal(StorageProvider.S3, target.Provider);
        Assert.Equal("lake", target.Container);
        Assert.Equal("exports/2026", target.Prefix);
        Assert.Equal("eu-central-1", target.Option("region"));
    }

    [Fact]
    public void An_azure_url_carries_the_account_as_well_as_the_container()
    {
        // A storage account has more than one container, so the connection has to say which.
        var target = StorageUrl.Parse("azblob://mystorage/exports/2026/08");

        Assert.Equal(StorageProvider.AzureBlob, target.Provider);
        Assert.Equal("mystorage", target.Account);
        Assert.Equal("exports", target.Container);
        Assert.Equal("2026/08", target.Prefix);
    }

    [Fact]
    public void An_azure_url_without_a_container_is_refused() =>
        Assert.Throws<FormatException>(() => StorageUrl.Parse("azblob://mystorage"));

    [Fact]
    public void A_file_url_is_a_path()
    {
        var target = StorageUrl.Parse("file:///data/incoming");

        Assert.Equal(StorageProvider.Local, target.Provider);
        Assert.Equal("/data/incoming", target.Container.Replace('\\', '/'));
        Assert.Equal("", target.Prefix);
    }

    [Fact]
    public void Credentials_are_optional_and_the_target_says_which_it_has()
    {
        Assert.False(StorageUrl.Parse("s3://lake?region=eu-central-1").HasExplicitCredentials);
        Assert.True(StorageUrl.Parse("s3://lake?access=AK&secret=SK").HasExplicitCredentials);
        Assert.True(StorageUrl.Parse("azblob://a/c?key=abc").HasExplicitCredentials);
        Assert.True(StorageUrl.Parse("azblob://a/c?sas=sv=2026").HasExplicitCredentials);
        Assert.False(StorageUrl.Parse("azblob://a/c").HasExplicitCredentials);
    }

    [Fact]
    public void A_prefix_scoped_connection_addresses_keys_inside_it()
    {
        var target = StorageUrl.Parse("s3://lake/exports/2026");

        Assert.Equal("exports/2026/orders.parquet", target.KeyOf("orders.parquet"));
        Assert.Equal("exports/2026/orders.parquet", target.KeyOf("/orders.parquet"));
    }

    [Fact]
    public void Something_that_is_not_a_url_says_so() =>
        Assert.Throws<FormatException>(() => StorageUrl.Parse("just some text"));

    [Fact]
    public void A_scheme_with_no_bucket_says_so() =>
        Assert.Throws<FormatException>(() => StorageUrl.Parse("s3:///prefix"));
}
