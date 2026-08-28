using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Editing;

namespace WebDataStudio.Server.Tests.Editing;

/// A file put into a binary cell. It leaves the server as `0x…` and has to come back as bytes —
/// written as text it would be a PNG nobody can open, saved without a complaint.
public class BinaryCellTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// A table with an id and one other column, which is all these statements need to be built.
    private static ObjectDetail Detail(string column, string type) => new(
        new SchemaNodeRef(SchemaNodeKind.Table, ["public", "files"]),
        [
            new ColumnInfo("id", "integer", false, null, true, false, null, 1),
            new ColumnInfo(column, type, true, null, false, false, null, 2),
        ],
        [], [], [], null, null, null, null);

    [Fact]
    public void The_text_form_a_binary_cell_travels_in_is_recognised()
    {
        Assert.True(BinaryValue.Looks("0x89504e470d0a1a0a"));
        Assert.True(BinaryValue.Looks("0X89"));

        // Odd length is not bytes, and neither is anything with a non-hex character in it.
        Assert.False(BinaryValue.Looks("0x8"));
        Assert.False(BinaryValue.Looks("0xzz"));
        Assert.False(BinaryValue.Looks("hello"));
        Assert.False(BinaryValue.Looks(null));
    }

    [Fact]
    public void And_parsed_back_into_the_bytes_it_came_from()
    {
        Assert.Equal(Png, BinaryValue.Parse("0x" + Convert.ToHexString(Png)));
        Assert.Null(BinaryValue.Parse("not bytes"));
    }

    [Fact]
    public void A_preview_says_how_big_it_is_rather_than_pasting_a_megabyte_of_hex()
    {
        var description = BinaryValue.Describe(new byte[4096]);

        Assert.Contains("4096 bytes", description);
        Assert.True(description.Length < 40);
    }

    [Fact]
    public void Each_engine_gets_the_binary_literal_it_understands()
    {
        Assert.Equal("0x89504e470d0a1a0a", BinaryValue.Literal(Png, new SqlServerDialect()));
        Assert.Equal(@"'\x89504e470d0a1a0a'::bytea", BinaryValue.Literal(Png, new PostgreSqlDialect()));
        Assert.Equal("X'89504e470d0a1a0a'", BinaryValue.Literal(Png, new SqliteDialect()));
    }

    /// The point of all of it: the update writes bytes, not the characters that spell them.
    [Fact]
    public void An_update_of_a_binary_column_writes_a_binary_literal()
    {
        var script = ChangeScriptBuilder.Build(
            new ChangeSet("c1", "Table:public/files", [new RowChange("update",
                new Dictionary<string, object?> { ["id"] = "1" },
                new Dictionary<string, object?> { ["blob"] = "0x" + Convert.ToHexString(Png) })]),
            Detail("blob", "bytea"), new PostgreSqlDialect());

        Assert.Contains(@"'\x89504e470d0a1a0a'::bytea", script.Statements[0].Sql);

        // No parameter was left behind for a value that is in the statement itself.
        Assert.DoesNotContain(script.Statements[0].Parameters, p => p.Key == "p0");
    }

    [Fact]
    public void Text_that_only_looks_like_hex_in_a_text_column_stays_a_parameter()
    {
        var script = ChangeScriptBuilder.Build(
            new ChangeSet("c1", "Table:public/files", [new RowChange("update",
                new Dictionary<string, object?> { ["id"] = "1" },
                new Dictionary<string, object?> { ["name"] = "0xdeadbeef" })]),
            Detail("name", "text"), new PostgreSqlDialect());

        Assert.Contains("@p0", script.Statements[0].Sql);
        Assert.Equal("0xdeadbeef", script.Statements[0].Parameters["p0"]);
    }
}
