using MongoDB.Bson;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MongoDb;
using WebDataStudio.Server.Drivers.Redis;

namespace WebDataStudio.Server.Tests.Drivers;

/// The studio has one filter language, and the data tab writes it the same way whatever is
/// underneath. These are the translations for the two engines that have no SQL to build it in.
public class MongoFilterTests
{
    private static BsonDocument Filter(string expression) => MongoPage.Filter("name", expression).Filter;

    [Fact]
    public void A_plain_word_means_contains()
    {
        var filter = Filter("ada");

        Assert.Equal(BsonType.RegularExpression, filter["name"].BsonType);
        Assert.Equal("ada", filter["name"].AsBsonRegularExpression.Pattern);
        Assert.Equal("i", filter["name"].AsBsonRegularExpression.Options);
    }

    [Fact]
    public void A_caret_anchors_at_the_start_and_a_dollar_at_the_end()
    {
        Assert.Equal("^ada", Filter("^ada")["name"].AsBsonRegularExpression.Pattern);
        Assert.Equal("ada$", Filter("$ada")["name"].AsBsonRegularExpression.Pattern);
    }

    /// A value with regex characters in it is a value, not a pattern: `a.b` must not match `axb`.
    [Fact]
    public void A_dot_in_the_value_stays_a_dot()
    {
        Assert.Equal(@"a\.b", Filter("a.b")["name"].AsBsonRegularExpression.Pattern);
    }

    [Fact]
    public void A_tilde_is_the_negation()
    {
        Assert.True(Filter("~ada")["name"].AsBsonDocument.Contains("$not"));
    }

    [Theory]
    [InlineData(">10", "$gt")]
    [InlineData("<10", "$lt")]
    [InlineData(">=10", "$gte")]
    [InlineData("<=10", "$lte")]
    [InlineData("!=10", "$ne")]
    [InlineData("=10", "$eq")]
    public void The_comparisons_become_their_operators(string expression, string op)
    {
        var condition = Filter(expression)["name"].AsBsonDocument;

        Assert.True(condition.Contains(op));

        // A number compares as a number: as text, 9 would be greater than 10.
        Assert.Equal(BsonType.Int64, condition[op].BsonType);
    }

    [Fact]
    public void A_list_of_alternatives_becomes_an_in()
    {
        var condition = Filter("=ada,=grace")["name"].AsBsonDocument;

        Assert.Equal(["ada", "grace"], condition["$in"].AsBsonArray.Select(value => value.AsString));
    }

    [Fact]
    public void Null_and_not_null_are_the_two_they_look_like()
    {
        Assert.Equal(BsonNull.Value, Filter("NULL")["name"]);
        Assert.Equal(BsonNull.Value, Filter("!NULL")["name"].AsBsonDocument["$ne"]);
    }

    /// A date period is SQL-side sugar. Silently matching "LAST MONTH" as a piece of text would be a
    /// filter that quietly finds nothing, so the page says what it did.
    [Fact]
    public void A_date_period_is_matched_as_text_and_says_so()
    {
        var (_, note) = MongoPage.Filter("created", "LAST MONTH");

        Assert.NotNull(note);
        Assert.Contains("date periods", note);
    }

    [Fact]
    public void An_empty_filter_is_no_filter()
    {
        Assert.Equal(0, MongoPage.Filter("name", "  ").Filter.ElementCount);
    }
}

public class MongoProjectionTests
{
    private static ColumnInfo Column(string name, int position) =>
        new(name, "string", true, null, false, false, null, position);

    [Fact]
    public void A_document_becomes_a_row_of_the_sampled_shape()
    {
        var (columns, rows) = MongoPage.Project(
            [new BsonDocument { ["_id"] = 1, ["name"] = "ada" }],
            [Column("_id", 1), Column("name", 2)]);

        Assert.Equal(["_id", "name"], columns.Select(column => column.Name));
        Assert.Equal([1, "ada"], Assert.Single(rows));
    }

    [Fact]
    public void A_field_the_document_does_not_have_is_null()
    {
        var (_, rows) = MongoPage.Project(
            [new BsonDocument { ["_id"] = 1 }],
            [Column("_id", 1), Column("name", 2)]);

        Assert.Null(Assert.Single(rows)[1]);
    }

    /// Documents have no schema, and the sample is a sample. A field this page turned up that the
    /// structure panel never saw is the interesting one — it must not be dropped on the floor.
    [Fact]
    public void A_field_the_sample_never_saw_gets_a_column_of_its_own()
    {
        var (columns, rows) = MongoPage.Project(
            [new BsonDocument { ["_id"] = 1, ["surprise"] = "here" }],
            [Column("_id", 1)]);

        Assert.Equal(["_id", "surprise"], columns.Select(column => column.Name));
        Assert.Equal("unsampled", columns[1].DataType);
        Assert.Equal("here", Assert.Single(rows)[1]);
    }

    [Fact]
    public void A_nested_document_stays_json_in_its_cell()
    {
        var (_, rows) = MongoPage.Project(
            [new BsonDocument { ["address"] = new BsonDocument { ["city"] = "london" } }],
            [Column("address", 1)]);

        Assert.Contains("london", (string?)Assert.Single(rows)[0]);
    }

    [Fact]
    public void An_object_id_arrives_as_the_string_people_paste()
    {
        var id = ObjectId.GenerateNewId();

        var (_, rows) = MongoPage.Project(
            [new BsonDocument { ["_id"] = id }], [Column("_id", 1)]);

        Assert.Equal(id.ToString(), Assert.Single(rows)[0]);
    }

    /// A grid cell is one line. A megabyte of base64 in it helps nobody.
    [Fact]
    public void Binary_says_how_big_it_is_rather_than_pasting_itself()
    {
        var (_, rows) = MongoPage.Project(
            [new BsonDocument { ["blob"] = new BsonBinaryData(new byte[10]) }], [Column("blob", 1)]);

        Assert.Equal("10 bytes", Assert.Single(rows)[0]);
    }
}

public class RedisFilterTests
{
    [Theory]
    [InlineData("user", "user:1", true)]
    [InlineData("USER", "user:1", true)]
    [InlineData("^user", "user:1", true)]
    [InlineData("^ser", "user:1", false)]
    [InlineData("$1", "user:1", true)]
    [InlineData("$2", "user:1", false)]
    [InlineData("~user", "user:1", false)]
    [InlineData("~session", "user:1", true)]
    [InlineData("=user:1", "user:1", true)]
    [InlineData("=user", "user:1", false)]
    [InlineData("  ", "user:1", true)]
    public void The_key_filter_reads_the_same_language(string filter, string key, bool matches) =>
        Assert.Equal(matches, RedisPage.Matches(key, filter));
}
