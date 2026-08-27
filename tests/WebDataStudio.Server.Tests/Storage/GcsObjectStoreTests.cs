using WebDataStudio.Server.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// Google Cloud Storage. The operations are checked against the emulator where one is available; the
/// part worth pinning down without a network is the stated limit: DuckDB reaches Google through the
/// S3 protocol, so querying needs HMAC keys even where browsing does not.
public class GcsObjectStoreTests
{
    private static GcsObjectStore Store(string url) => new(StorageUrl.Parse(url));

    [Fact]
    public void The_uri_is_the_one_duckdb_reads() =>
        Assert.Equal("gs://lake/exports/2026/a.csv",
            Store("gs://lake?credentials={}").SqlUri("exports/2026/a.csv"));

    [Fact]
    public void A_prefix_scoped_connection_addresses_inside_its_prefix() =>
        Assert.Equal("gs://lake/exports/2026/a.csv",
            Store("gs://lake/exports/2026?credentials={}").SqlUri("a.csv"));

    [Fact]
    public void Hmac_keys_become_the_secret()
    {
        var secret = Store("gs://lake?credentials={}&hmac=GOOG1E&hmacsecret=abc").SecretStatement();

        Assert.Contains("TYPE gcs", secret);
        Assert.Contains("KEY_ID 'GOOG1E'", secret);
        Assert.Contains("SECRET 'abc'", secret);
    }

    [Fact]
    public void Without_hmac_keys_there_is_no_secret_to_make()
    {
        // Browsing works through the service account; a query does not, and the documentation says
        // so rather than the studio pretending otherwise.
        Assert.Null(Store("gs://lake?credentials={}").SecretStatement());
    }

    [Fact]
    public void A_quote_in_a_key_cannot_end_the_sql_string()
    {
        var secret = Store("gs://lake?credentials={}&hmac=a'b&hmacsecret=c'd").SecretStatement();

        Assert.Contains("KEY_ID 'a''b'", secret);
        Assert.Contains("SECRET 'c''d'", secret);
    }
}
