using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Drivers;

/// The single behaviour suite every engine must satisfy. Derive one class per engine; the fixture
/// seeds a `people` table (id, name, active) with three rows and an `orders` table with a foreign
/// key to it.
public abstract class DriverContractTests<TFixture> : IClassFixture<TFixture>
    where TFixture : class, IDriverFixture
{
    private readonly TFixture _fixture;
    protected DriverContractTests(TFixture fixture) => _fixture = fixture;

    private IDbDriver Driver => _fixture.Driver;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IDbSession> OpenAsync() => await Driver.OpenAsync(_fixture.Spec, Ct);

    [Fact]
    public async Task Opens_a_session()
    {
        await using var session = await OpenAsync();
        Assert.Equal(System.Data.ConnectionState.Open, session.Connection.State);
    }

    [Fact]
    public async Task Root_introspection_returns_children()
    {
        await using var session = await OpenAsync();
        Assert.NotEmpty(await Driver.IntrospectAsync(session, null, Ct));
    }

    [Fact]
    public async Task Finds_the_seeded_table()
    {
        await using var session = await OpenAsync();
        Assert.NotNull(await FindObjectAsync(session, "people"));
    }

    [Fact]
    public async Task Describes_columns_with_the_primary_key_marked()
    {
        await using var session = await OpenAsync();
        var table = await FindObjectAsync(session, "people");
        var detail = await Driver.DescribeAsync(session, table!.Ref, Ct);

        Assert.Contains(detail.Columns, c => c.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(detail.Columns, c => c.IsPrimaryKey);
    }

    [Fact]
    public async Task Describes_the_foreign_key_of_the_orders_table()
    {
        if (!Driver.Caps.ForeignKeys) return;

        await using var session = await OpenAsync();
        var orders = await FindObjectAsync(session, "orders");
        var detail = await Driver.DescribeAsync(session, orders!.Ref, Ct);

        var fk = Assert.Single(detail.ForeignKeys);
        Assert.Equal("people", fk.ReferencedTable, ignoreCase: true);
    }

    [Fact]
    public async Task Executes_a_select_and_streams_columns_then_rows()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT 1 AS one");

        Assert.Contains(chunks, c => c is ResultChunk.Columns);
        var rows = chunks.OfType<ResultChunk.Rows>().SelectMany(r => r.Items).ToList();
        Assert.Equal(1, Convert.ToInt32(Assert.Single(rows)[0]));
        Assert.Contains(chunks, c => c is ResultChunk.End);
    }

    [Fact]
    public async Task Reads_all_three_seeded_rows()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT id FROM people");
        Assert.Equal(3, chunks.OfType<ResultChunk.Rows>().Sum(r => r.Items.Count));
    }

    [Fact]
    public async Task Honours_the_row_cap_and_flags_truncation()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT id FROM people", maxRows: 2);

        Assert.Equal(2, chunks.OfType<ResultChunk.Rows>().Sum(r => r.Items.Count));
        Assert.True(chunks.OfType<ResultChunk.End>().Single().Truncated);
    }

    [Fact]
    public async Task Executes_several_statements_and_numbers_them()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT 1; SELECT 2;");

        Assert.Equal(2, chunks.OfType<ResultChunk.End>().Count());
        Assert.Contains(chunks, c => c.Statement == 1);
    }

    [Fact]
    public async Task Reports_a_syntax_error_as_an_error_chunk()
    {
        await using var session = await OpenAsync();
        Assert.Contains(await CollectAsync(session, "SELECT FROM WHERE"), c => c is ResultChunk.Error);
    }

    [Fact]
    public async Task A_read_only_connection_rejects_a_write()
    {
        await using var session = await Driver.OpenAsync(_fixture.Spec with { ReadOnly = true }, Ct);
        var chunks = await CollectAsync(session, "DELETE FROM people");

        var error = Assert.Single(chunks.OfType<ResultChunk.Error>());
        Assert.Contains("read-only", error.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        await using var session = await OpenAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ScriptRequest("SELECT id FROM people", MaxRows: 1000, TimeoutSeconds: 30);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Driver.ExecuteAsync(session, request, cts.Token)) { }
        });
    }

    [Fact]
    public async Task Explain_returns_a_plan_or_throws_when_unsupported()
    {
        await using var session = await OpenAsync();
        if (Driver.Caps.EstimatedPlan)
        {
            var plan = await Driver.ExplainAsync(session, "SELECT * FROM people", PlanMode.Estimated, Ct);
            Assert.NotNull(plan);
            Assert.NotEmpty(plan.Operation);
        }
        else
        {
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                Driver.ExplainAsync(session, "SELECT 1", PlanMode.Estimated, Ct));
        }
    }

    [Fact]
    public async Task Actual_plan_is_supported_or_throws()
    {
        await using var session = await OpenAsync();
        if (Driver.Caps.ActualPlan) return;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Driver.ExplainAsync(session, "SELECT 1", PlanMode.Actual, Ct));
    }

    [Fact]
    public void Dialect_quotes_identifiers_reversibly()
    {
        var quoted = Driver.Dialect.QuoteIdentifier("we ird");
        Assert.Contains("we ird", quoted);
        Assert.NotEqual("we ird", quoted);
    }

    [Fact]
    public void Dialect_classifies_writes_as_not_read_only()
    {
        Assert.True(Driver.Dialect.IsReadOnlyStatement("SELECT 1"));
        Assert.True(Driver.Dialect.IsReadOnlyStatement("-- comment\nSELECT 1"));
        Assert.False(Driver.Dialect.IsReadOnlyStatement("DELETE FROM people"));
        Assert.False(Driver.Dialect.IsReadOnlyStatement("-- comment\nDROP TABLE people"));
    }

    // --- helpers -----------------------------------------------------------

    private async Task<List<ResultChunk>> CollectAsync(IDbSession session, string sql, int maxRows = 1000)
    {
        var chunks = new List<ResultChunk>();
        var request = new ScriptRequest(sql, maxRows, TimeoutSeconds: 30, Schema: _fixture.Schema);
        await foreach (var chunk in Driver.ExecuteAsync(session, request, Ct)) chunks.Add(chunk);
        return chunks;
    }

    /// Walks the tree breadth-first until it finds a table with the given name. Engines differ in
    /// how deep tables sit, so the contract suite must not assume a fixed depth.
    private async Task<SchemaNode?> FindObjectAsync(IDbSession session, string name)
    {
        var queue = new Queue<SchemaNodeRef?>();
        queue.Enqueue(null);
        var visited = 0;

        while (queue.Count > 0 && visited++ < 200)
        {
            var parent = queue.Dequeue();
            foreach (var node in await Driver.IntrospectAsync(session, parent, Ct))
            {
                if (node.Ref.Kind == SchemaNodeKind.Table &&
                    node.Ref.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return node;
                if (node.HasChildren && node.Ref.Kind is not (SchemaNodeKind.Table or SchemaNodeKind.View))
                    queue.Enqueue(node.Ref);
            }
        }
        return null;
    }
}
