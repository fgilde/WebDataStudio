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

    [Fact]
    public async Task Explain_is_refused_because_there_is_no_planner()
    {
        await using var session = await fixture.Driver.OpenAsync(fixture.Spec, Ct);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Driver.ExplainAsync(session, "GET x", PlanMode.Estimated, Ct));
    }
}
