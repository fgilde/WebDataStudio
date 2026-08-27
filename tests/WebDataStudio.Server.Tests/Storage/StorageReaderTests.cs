using WebDataStudio.Server.Drivers.Storage;

namespace WebDataStudio.Server.Tests.Storage;

/// Which reader reads which file. Decided by the name, so it can be decided without a network.
public class StorageReaderTests
{
    [Theory]
    [InlineData("s3://lake/a.parquet", "read_parquet('s3://lake/a.parquet')")]
    [InlineData("s3://lake/a.csv", "read_csv_auto('s3://lake/a.csv')")]
    [InlineData("az://exports/a.tsv", "read_csv_auto('az://exports/a.tsv')")]
    [InlineData("gs://lake/a.ndjson", "read_json_auto('gs://lake/a.ndjson')")]
    // Compression is DuckDB's business; what is under it decides the reader.
    [InlineData("s3://lake/a.csv.gz", "read_csv_auto('s3://lake/a.csv.gz')")]
    // A whole folder is one table, which is the point of a glob.
    [InlineData("s3://lake/exports/*.parquet", "read_parquet('s3://lake/exports/*.parquet')")]
    public void A_readable_name_becomes_a_reader_call(string uri, string expected) =>
        Assert.Equal(expected, StorageReader.Call(uri));

    [Theory]
    [InlineData("s3://lake/notes.zip")]
    [InlineData("s3://lake/photo.png")]
    [InlineData("s3://lake/README")]
    public void Anything_else_has_no_from_clause_at_all(string uri)
    {
        // Not an error: the menu then offers a preview and a download rather than a failing query.
        Assert.Null(StorageReader.Call(uri));
        Assert.False(StorageReader.CanRead(uri));
    }

    [Fact]
    public void A_quote_in_a_key_cannot_end_the_sql_string() =>
        Assert.Equal("read_csv_auto('s3://lake/it''s.csv')", StorageReader.Call("s3://lake/it's.csv"));
}
