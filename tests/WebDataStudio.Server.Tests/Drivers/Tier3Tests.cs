using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;
using Testcontainers.MongoDb;
using Testcontainers.Redis;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MongoDb;
using WebDataStudio.Server.Drivers.Redis;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public class MongoCommandParserTests
{
    [Fact]
    public void Parses_a_find_with_a_filter()
    {
        var command = MongoCommandParser.Parse("db.people.find({ active: true })");

        Assert.Equal("people", command.Collection);
        Assert.Equal("find", command.Operation);
        Assert.Single(command.Arguments);
    }

    [Fact]
    public void Accepts_relaxed_json()
    {
        var command = MongoCommandParser.Parse("db.people.find({ name: 'ada' })");
        Assert.Equal("ada", command.Arguments[0]["name"].AsString);
    }

    [Fact]
    public void Parses_an_aggregation_pipeline_into_stages()
    {
        var command = MongoCommandParser.Parse(
            "db.people.aggregate([{ $match: { active: true } }, { $count: 'n' }])");

        Assert.Equal("aggregate", command.Operation);
        Assert.Equal(2, command.Arguments.Count);
    }

    [Fact]
    public void Carries_the_limit_modifier()
    {
        Assert.Equal(10, MongoCommandParser.Parse("db.people.find({}).limit(10)").Limit);
    }

    [Fact]
    public void Carries_skip_and_sort()
    {
        var command = MongoCommandParser.Parse("db.people.find({}).skip(5).sort({ name: 1 })");

        Assert.Equal(5, command.Skip);
        Assert.NotNull(command.Sort);
    }

    [Fact]
    public void An_unknown_operation_is_rejected_by_name()
    {
        var error = Assert.Throws<NotSupportedException>(() => MongoCommandParser.Parse("db.people.frobnicate({})"));
        Assert.Contains("frobnicate", error.Message);
    }

    [Fact]
    public void Something_that_is_not_a_command_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => MongoCommandParser.Parse("SELECT * FROM people"));
    }

    [Theory]
    [InlineData("db.people.find({})", false)]
    [InlineData("db.people.countDocuments({})", false)]
    [InlineData("db.people.insertOne({})", true)]
    [InlineData("db.people.deleteMany({})", true)]
    [InlineData("db.people.drop()", true)]
    public void Classifies_writes(string command, bool isWrite) =>
        Assert.Equal(isWrite, MongoCommandParser.Parse(command).IsWrite);
}

public class RedisCommandTests
{
    [Theory]
    [InlineData("GET key", true)]
    [InlineData("hgetall key", true)]
    [InlineData("SCAN 0", true)]
    [InlineData("SET key value", false)]
    [InlineData("DEL key", false)]
    [InlineData("FLUSHALL", false)]
    public void Classifies_reads(string command, bool isRead) =>
        Assert.Equal(isRead, RedisCommands.IsRead(command));
}

public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder().WithImage("mongo:7").Build();

    public IDbDriver Driver { get; } = new MongoDbDriver();
    // The container hands out a URL with query parameters, so the database goes in through the
    // URL builder rather than by appending "/shop".
    // Setting the database also moves the authentication source, so it is pinned back to admin —
    // exactly the trap a real user hits when they paste a URL with a database in it.
    public ConnectionSpec Spec => new("t", "test", "mongodb",
        new MongoUrlBuilder(_container.GetConnectionString())
        {
            DatabaseName = "shop",
            AuthenticationSource = "admin",
        }.ToString(),
        false, null, null, ConnectionSource.Stored);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var client = new MongoClient(_container.GetConnectionString());
        var database = client.GetDatabase("shop");
        await database.GetCollection<BsonDocument>("people").InsertManyAsync(
        [
            new BsonDocument { ["_id"] = 1, ["name"] = "ada", ["active"] = true },
            new BsonDocument { ["_id"] = 2, ["name"] = "linus", ["active"] = true },
            new BsonDocument { ["_id"] = 3, ["name"] = "grace", ["active"] = false },
        ]);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class MongoDriverTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Declares_itself_as_a_non_sql_engine() => Assert.False(fixture.Driver.Caps.Sql);

    [Fact]
    public async Task Lists_databases_and_collections()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var databases = await fixture.Driver.IntrospectAsync(session, null, Ct);
        var shop = Assert.Single(databases, d => d.Label == "shop");

        var collections = await fixture.Driver.IntrospectAsync(session, shop.Ref, Ct);
        Assert.Contains(collections, c => c.Label == "people");
    }

    [Fact]
    public async Task Samples_the_document_shape()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);
        var detail = await fixture.Driver.DescribeAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["shop", "people"]), Ct);

        Assert.Contains(detail.Columns, c => c.Name == "name");
        Assert.Contains(detail.Columns, c => c is { Name: "_id", IsPrimaryKey: true });
        Assert.Equal(3, detail.RowCount);
    }

    [Fact]
    public async Task Runs_a_find_and_returns_documents()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in fixture.Driver.ExecuteAsync(session,
            new ScriptRequest("db.people.find({ active: true })", 100, 30, "shop"), Ct))
            chunks.Add(chunk);

        var documents = chunks.OfType<ResultChunk.Documents>().SelectMany(d => d.Items).ToList();
        Assert.Equal(2, documents.Count);
        Assert.Contains(documents, d => d.GetProperty("name").GetString() == "ada");
    }

    [Fact]
    public async Task Runs_an_aggregation()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in fixture.Driver.ExecuteAsync(session,
            new ScriptRequest("db.people.aggregate([{ $match: { active: true } }, { $count: 'total' }])",
                100, 30, "shop"), Ct))
            chunks.Add(chunk);

        var document = Assert.Single(chunks.OfType<ResultChunk.Documents>().SelectMany(d => d.Items));
        Assert.Equal(2, document.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_read_only_connection_refuses_a_write()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec with { ReadOnly = true }, Ct);

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in fixture.Driver.ExecuteAsync(session,
            new ScriptRequest("db.people.deleteMany({})", 100, 30, "shop"), Ct))
            chunks.Add(chunk);

        var error = Assert.Single(chunks.OfType<ResultChunk.Error>());
        Assert.Contains("read-only", error.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_malformed_command_arrives_as_an_error_chunk()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in fixture.Driver.ExecuteAsync(session,
            new ScriptRequest("SELECT * FROM people", 100, 30, "shop"), Ct))
            chunks.Add(chunk);

        Assert.Single(chunks.OfType<ResultChunk.Error>());
    }

    /// The data tab asks every engine for a page of rows. It used to build `SELECT * FROM "people"`
    /// for MongoDB, which came back as "this is not a MongoDB command" — the driver builds the find
    /// itself now.
    [Fact]
    public async Task A_page_of_documents_arrives_as_rows()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["shop", "people"]),
            new PageQuery(0, 2, "name", false, null, null), Ct);

        Assert.NotNull(page);
        Assert.Contains(page.Columns, column => column.Name == "name");
        Assert.Equal(2, page.Rows.Count);

        // Sorted by the engine, not by the page: ada before grace.
        var name = page.Columns.ToList().FindIndex(column => column.Name == "name");
        Assert.Equal("ada", page.Rows[0][name]);

        // The whole collection is the total, not the page.
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task Paging_skips_the_documents_it_was_told_to()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["shop", "people"]),
            new PageQuery(2, 2, "name", false, null, null), Ct);

        Assert.NotNull(page);
        Assert.Single(page.Rows);
    }

    [Fact]
    public async Task A_filter_becomes_part_of_the_find()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["shop", "people"]),
            new PageQuery(0, 50, null, false, "name", "=ada"), Ct);

        Assert.NotNull(page);
        Assert.Single(page.Rows);

        // Filtered means counted: the total is what the filter left, not what the collection holds.
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task A_document_is_not_edited_in_the_grid_and_the_page_says_why()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["shop", "people"]),
            new PageQuery(0, 10, null, false, null, null), Ct);

        Assert.NotNull(page);
        Assert.False(page.Editable);
        Assert.Contains("updateOne", page.Reason);
    }

    /// A collection is a table; the database above it is not.
    [Fact]
    public async Task A_database_has_no_page_of_its_own()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        Assert.Null(await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Schema, ["shop"]),
            new PageQuery(0, 10, null, false, null, null), Ct));
    }

    [Fact]
    public async Task Explain_marks_a_collection_scan()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);
        var plan = await fixture.Driver.ExplainAsync(session,
            "db.people.find({ active: true })", PlanMode.Estimated, Ct);

        Assert.NotEmpty(plan.Operation);
    }
}

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();

    public IDbDriver Driver { get; } = new RedisDriver();
    public ConnectionSpec Spec => new("t", "test", "redis", _container.GetConnectionString(),
        false, null, null, ConnectionSource.Stored);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        var db = multiplexer.GetDatabase();

        await db.StringSetAsync("app:greeting", "hello");
        await db.HashSetAsync("app:user:1", [new HashEntry("name", "ada")]);
        await db.ListRightPushAsync("app:queue", ["a", "b"]);
        await db.SetAddAsync("app:tags", ["x", "y"]);
        await db.SortedSetAddAsync("app:scores", "ada", 10);

        await multiplexer.CloseAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class RedisDriverTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Declares_itself_as_a_non_sql_engine() => Assert.False(fixture.Driver.Caps.Sql);

    [Fact]
    public async Task Groups_keys_by_their_prefix()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var databases = await fixture.Driver.IntrospectAsync(session, null, Ct);
        var children = await fixture.Driver.IntrospectAsync(session, databases[0].Ref, Ct);

        Assert.Contains(children, c => c.Label == "app" && c.HasChildren);
    }

    [Fact]
    public async Task Reports_the_type_and_length_of_a_key()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);
        var detail = await fixture.Driver.DescribeAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["0", "app", "greeting"]), Ct);

        Assert.Contains(detail.Columns, c => c.Name == "type" && c.DataType == "string");
        Assert.Equal(5, detail.RowCount);
    }

    [Fact]
    public async Task Runs_a_command_and_returns_a_document()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in fixture.Driver.ExecuteAsync(session,
            new ScriptRequest("GET app:greeting", 100, 30), Ct))
            chunks.Add(chunk);

        var document = Assert.Single(chunks.OfType<ResultChunk.Documents>().SelectMany(d => d.Items));
        Assert.Equal("hello", document.GetString());
    }

    [Fact]
    public async Task A_read_only_connection_refuses_a_write()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec with { ReadOnly = true }, Ct);

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in fixture.Driver.ExecuteAsync(session,
            new ScriptRequest("SET app:greeting bye", 100, 30), Ct))
            chunks.Add(chunk);

        var error = Assert.Single(chunks.OfType<ResultChunk.Error>());
        Assert.Contains("read-only", error.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// A key space is a table if you look at it right, and this is the table people actually want:
    /// what is in here, of what type, expiring when, how big.
    [Fact]
    public async Task A_key_space_is_a_table_of_keys()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Schema, ["0"]),
            new PageQuery(0, 100, null, false, null, null), Ct);

        Assert.NotNull(page);
        Assert.Equal(["key", "type", "ttl", "length", "memory"],
            page.Columns.Select(column => column.Name));

        var keys = page.Rows.Select(row => (string?)row[0]).ToList();
        Assert.Contains("app:greeting", keys);
        Assert.Contains("app:scores", keys);

        var greeting = page.Rows.First(row => (string?)row[0] == "app:greeting");
        Assert.Equal("string", greeting[1]);
        Assert.Null(greeting[2]);   // no TTL was set
        Assert.Equal(5L, greeting[3]);
    }

    [Fact]
    public async Task A_prefix_folder_pages_only_what_is_under_it()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.TableFolder, ["0", "app"]),
            new PageQuery(0, 100, "key", true, "key", "^app:s"), Ct);

        Assert.NotNull(page);
        Assert.Equal(["app:scores"], page.Rows.Select(row => (string?)row[0]));
    }

    [Fact]
    public async Task A_hash_key_is_field_and_value()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["0", "app", "user", "1"]),
            new PageQuery(0, 100, null, false, null, null), Ct);

        Assert.NotNull(page);
        Assert.Equal(["field", "value"], page.Columns.Select(column => column.Name));
        Assert.Equal(["name", "ada"], Assert.Single(page.Rows).Select(cell => (string?)cell));
        Assert.Contains("HSET", page.Reason);
    }

    [Fact]
    public async Task A_sorted_set_is_member_and_score()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["0", "app", "scores"]),
            new PageQuery(0, 100, null, false, null, null), Ct);

        Assert.NotNull(page);
        Assert.Equal(["member", "score"], page.Columns.Select(column => column.Name));
        Assert.Equal(10d, Assert.Single(page.Rows)[1]);
    }

    [Fact]
    public async Task A_list_keeps_its_order_and_says_so_with_an_index()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["0", "app", "queue"]),
            new PageQuery(0, 100, null, false, null, null), Ct);

        Assert.NotNull(page);
        Assert.Equal(["index", "value"], page.Columns.Select(column => column.Name));
        Assert.Equal([0L, 1L], page.Rows.Select(row => row[0]));
        Assert.Equal("a", page.Rows[0][1]);
    }

    [Fact]
    public async Task A_set_is_its_members()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["0", "app", "tags"]),
            new PageQuery(0, 100, "member", false, null, null), Ct);

        Assert.NotNull(page);
        Assert.Equal(["member"], page.Columns.Select(column => column.Name));
        Assert.Equal(["x", "y"], page.Rows.Select(row => (string?)row[0]));
    }

    [Fact]
    public async Task A_string_key_is_its_value_and_its_length()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        var page = await fixture.Driver.PageAsync(session,
            new SchemaNodeRef(SchemaNodeKind.Table, ["0", "app", "greeting"]),
            new PageQuery(0, 100, null, false, null, null), Ct);

        Assert.NotNull(page);
        Assert.Equal(["hello", 5L], Assert.Single(page.Rows));
    }

    [Fact]
    public async Task Explain_is_refused_because_there_is_no_planner()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Driver.ExplainAsync(session, "GET x", PlanMode.Estimated, Ct));
    }
}
