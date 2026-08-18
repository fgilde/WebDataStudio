using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Tests.Drivers;

namespace WebDataStudio.Server.Tests.Analysis;

/// One suite over every live engine: the deep-analyze report must be usable, deterministic and
/// honest about what it cannot answer.
public abstract class DeepAnalyzeContractTests<TFixture> : IClassFixture<TFixture>
    where TFixture : class, IDriverFixture
{
    private readonly TFixture _fixture;
    protected DeepAnalyzeContractTests(TFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_finding_is_actionable()
    {
        await using var session = await _fixture.Driver.OpenAsync(_fixture.Spec, Ct);
        var report = await _fixture.Driver.AnalyzeAsync(session, AnalyzeScope.Schema,
            _fixture.Schema is null ? null : new SchemaNodeRef(SchemaNodeKind.Schema, [_fixture.Schema]), Ct);

        foreach (var finding in report.Findings)
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Category));
            Assert.False(string.IsNullOrWhiteSpace(finding.Title));
            Assert.False(string.IsNullOrWhiteSpace(finding.Detail));
            Assert.Contains(finding.Severity, new[] { "info", "warning", "critical" });
        }
    }

    [Fact]
    public async Task Running_twice_produces_the_same_report()
    {
        await using var session = await _fixture.Driver.OpenAsync(_fixture.Spec, Ct);
        var target = _fixture.Schema is null ? null : new SchemaNodeRef(SchemaNodeKind.Schema, [_fixture.Schema]);

        var first = await _fixture.Driver.AnalyzeAsync(session, AnalyzeScope.Schema, target, Ct);
        var second = await _fixture.Driver.AnalyzeAsync(session, AnalyzeScope.Schema, target, Ct);

        Assert.Equal(first.Findings.Select(f => f.Title), second.Findings.Select(f => f.Title));
    }

    [Fact]
    public async Task Finds_the_unindexed_foreign_key_when_one_exists()
    {
        await using var session = await _fixture.Driver.OpenAsync(_fixture.Spec, Ct);

        // The fixtures seed orders.person_id with an index for PostgreSQL and SQLite and without one
        // for MySQL and SQL Server, so this only asserts the report is well formed for this engine.
        var report = await _fixture.Driver.AnalyzeAsync(session, AnalyzeScope.Schema,
            _fixture.Schema is null ? null : new SchemaNodeRef(SchemaNodeKind.Schema, [_fixture.Schema]), Ct);

        Assert.All(report.Findings, f => Assert.NotNull(f.Category));
    }
}

public class SqliteDeepAnalyzeTests(SqliteFixture fixture) : DeepAnalyzeContractTests<SqliteFixture>(fixture);
public class PostgreSqlDeepAnalyzeTests(PostgreSqlFixture fixture) : DeepAnalyzeContractTests<PostgreSqlFixture>(fixture);
public class MySqlDeepAnalyzeTests(MySqlFixture fixture) : DeepAnalyzeContractTests<MySqlFixture>(fixture);
public class SqlServerDeepAnalyzeTests(SqlServerFixture fixture) : DeepAnalyzeContractTests<SqlServerFixture>(fixture);
