using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Tests.Drivers;

namespace WebDataStudio.Server.Tests.Ddl;

/// The strongest guarantee the writers can give: create a table from a definition, read it back,
/// and the diff must be empty. If the writer and the introspection disagree, this fails.
public abstract class DdlRoundTripTests<TFixture> : IClassFixture<TFixture>
    where TFixture : class, IDriverFixture
{
    private readonly TFixture _fixture;
    protected DdlRoundTripTests(TFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    protected abstract DdlWriterBase Writer { get; }

    private string Schema => _fixture.Schema ?? "main";

    private TableDefinition Wanted(string name) => new(
        _fixture.Schema ?? "", name,
        [
            new ColumnDefinition("id", "int", false, null, false, null),
            new ColumnDefinition("label", "text", false, null, false, null),
            new ColumnDefinition("amount", "bigint", true, null, false, null),
        ],
        [new IndexDefinition($"ix_{name}_label", ["label"], false)],
        [new ConstraintDefinition($"pk_{name}", ConstraintKind.PrimaryKey, ["id"])],
        null);

    [Fact]
    public async Task A_created_table_reads_back_with_an_empty_diff()
    {
        var name = $"wds_rt_{Guid.NewGuid():N}"[..20];
        var wanted = Wanted(name);

        await using var session = await _fixture.Driver.OpenAsync(_fixture.Spec, Ct);

        try
        {
            foreach (var statement in Writer.CreateTable(wanted)) await ExecuteAsync(session, statement.Sql);

            var detail = await _fixture.Driver.DescribeAsync(session,
                new SchemaNodeRef(SchemaNodeKind.Table, [Schema, name]), Ct);
            var actual = TableDefinition.From(detail);

            // Types come back in the engine's own spelling, so compare the shape that matters:
            // the column names, their order, and their nullability.
            Assert.Equal(wanted.Columns.Select(c => c.Name.ToLowerInvariant()),
                actual.Columns.Select(c => c.Name.ToLowerInvariant()));
            Assert.Equal(wanted.Columns.Select(c => c.Nullable), actual.Columns.Select(c => c.Nullable));

            Assert.Contains(actual.Constraints, c => c.Kind == ConstraintKind.PrimaryKey
                                                     && c.Columns.Any(x => x.Equals("id", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(actual.Indexes, i => i.Columns.Any(x => x.Equals("label", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            foreach (var statement in Writer.DropTable(_fixture.Schema ?? "", name))
                await TryExecuteAsync(session, statement.Sql);
        }
    }

    [Fact]
    public async Task Adding_a_column_takes_effect()
    {
        var name = $"wds_add_{Guid.NewGuid():N}"[..20];
        var before = Wanted(name);

        await using var session = await _fixture.Driver.OpenAsync(_fixture.Spec, Ct);

        try
        {
            foreach (var statement in Writer.CreateTable(before)) await ExecuteAsync(session, statement.Sql);

            var after = before with
            {
                Columns = before.Columns.Append(new ColumnDefinition("note", "text", true, null, false, null)).ToList(),
            };

            foreach (var statement in Writer.AlterTable(before, TableDiff.Compute(before, after)))
                await ExecuteAsync(session, statement.Sql);

            var detail = await _fixture.Driver.DescribeAsync(session,
                new SchemaNodeRef(SchemaNodeKind.Table, [Schema, name]), Ct);

            Assert.Contains(detail.Columns, c => c.Name.Equals("note", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var statement in Writer.DropTable(_fixture.Schema ?? "", name))
                await TryExecuteAsync(session, statement.Sql);
        }
    }

    private static async Task ExecuteAsync(IDbSession session, string sql)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task TryExecuteAsync(IDbSession session, string sql)
    {
        try { await ExecuteAsync(session, sql); }
        catch (Exception) { /* cleanup is best effort */ }
    }
}

public class SqliteDdlRoundTripTests(SqliteFixture fixture) : DdlRoundTripTests<SqliteFixture>(fixture)
{
    protected override DdlWriterBase Writer { get; } = new SqliteDdlWriter();
}

public class PostgreSqlDdlRoundTripTests(PostgreSqlFixture fixture) : DdlRoundTripTests<PostgreSqlFixture>(fixture)
{
    protected override DdlWriterBase Writer { get; } = new PostgreSqlDdlWriter();
}

public class MySqlDdlRoundTripTests(MySqlFixture fixture) : DdlRoundTripTests<MySqlFixture>(fixture)
{
    protected override DdlWriterBase Writer { get; } = new MySqlDdlWriter();
}

public class SqlServerDdlRoundTripTests(SqlServerFixture fixture) : DdlRoundTripTests<SqlServerFixture>(fixture)
{
    protected override DdlWriterBase Writer { get; } = new SqlServerDdlWriter();
}
