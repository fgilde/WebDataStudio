using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;

namespace WebDataStudio.Server.Tests.Analysis;

public class PlanRulesTests
{
    private static PlanNode Node(string operation, string? detail = null, double? estimatedRows = null,
        double? actualRows = null, double? cost = null, IReadOnlyList<PlanNode>? children = null,
        IReadOnlyList<string>? warnings = null) =>
        new(operation, detail, cost, estimatedRows, actualRows, null, children ?? [], warnings ?? []);

    [Fact]
    public void Flags_a_large_sequential_scan()
    {
        var findings = PlanRules.Evaluate(Node("Seq Scan", "people", estimatedRows: 50_000));

        var finding = Assert.Single(findings);
        Assert.Equal("missing-index", finding.Category);
        Assert.Contains("people", finding.Title);
    }

    [Fact]
    public void Leaves_a_small_scan_alone()
    {
        Assert.Empty(PlanRules.Evaluate(Node("Seq Scan", "people", estimatedRows: 20)));
    }

    [Fact]
    public void Flags_a_nested_loop_over_a_scan()
    {
        var plan = Node("Nested Loop", children:
            [Node("Index Scan", "people"), Node("Seq Scan", "orders", estimatedRows: 100)]);

        Assert.Contains(PlanRules.Evaluate(plan), f => f.Category == "nested-loop-scan");
    }

    [Fact]
    public void Flags_a_row_estimate_that_is_off_by_an_order_of_magnitude()
    {
        var findings = PlanRules.Evaluate(
            Node("Index Scan", "people", estimatedRows: 10, actualRows: 5_000));

        var finding = Assert.Single(findings, f => f.Category == "stale-statistics");
        Assert.Contains("ANALYZE", finding.Statement);
    }

    [Fact]
    public void Passes_through_engine_warnings()
    {
        var findings = PlanRules.Evaluate(Node("Sort", warnings: ["sort spilled to disk"]));

        var finding = Assert.Single(findings);
        Assert.Equal("plan-warning", finding.Category);
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public void A_clean_index_plan_yields_nothing()
    {
        Assert.Empty(PlanRules.Evaluate(Node("Index Scan", "people", estimatedRows: 3, actualRows: 3)));
    }

    [Fact]
    public void Summary_finds_the_most_expensive_node_and_counts_every_node()
    {
        var plan = Node("Hash Join", cost: 100, children:
            [Node("Seq Scan", "a", cost: 90), Node("Index Scan", "b", cost: 5)]);

        var summary = PlanSummaryBuilder.Summarize(plan);

        Assert.Equal(3, summary.NodeCount);
        Assert.Equal(100, summary.MaxNodeCost);
        Assert.Equal("Hash Join", summary.Hottest!.Operation);
    }
}

public class PredicateExtractorTests
{
    [Fact]
    public void Finds_an_equality_predicate()
    {
        var predicates = PredicateExtractor.Extract("SELECT * FROM people WHERE active = true");

        Assert.Contains(predicates, p => p.Column == "active" && p.Kind == PredicateKind.Equality
                                         && p.Table == "people");
    }

    [Fact]
    public void Finds_a_range_predicate()
    {
        var predicates = PredicateExtractor.Extract("SELECT * FROM people WHERE created_at > '2026-01-01'");
        Assert.Contains(predicates, p => p.Column == "created_at" && p.Kind == PredicateKind.Range);
    }

    [Fact]
    public void Finds_join_predicates_on_both_sides()
    {
        var predicates = PredicateExtractor.Extract(
            "SELECT * FROM people p JOIN orders o ON o.person_id = p.id");

        Assert.Contains(predicates, x => x.Table == "orders" && x.Column == "person_id" && x.Kind == PredicateKind.Join);
        Assert.Contains(predicates, x => x.Table == "people" && x.Column == "id" && x.Kind == PredicateKind.Join);
    }

    [Fact]
    public void Finds_order_by_and_group_by_columns()
    {
        Assert.Contains(PredicateExtractor.Extract("SELECT * FROM people ORDER BY name"),
            p => p.Column == "name" && p.Kind == PredicateKind.OrderBy);

        Assert.Contains(PredicateExtractor.Extract("SELECT status, count(*) FROM people GROUP BY status"),
            p => p.Column == "status" && p.Kind == PredicateKind.GroupBy);
    }

    [Fact]
    public void Ignores_a_predicate_inside_a_string_literal()
    {
        var predicates = PredicateExtractor.Extract("SELECT * FROM people WHERE name = 'x = y'");
        Assert.DoesNotContain(predicates, p => p.Column == "y");
    }

    [Fact]
    public void Ignores_a_predicate_inside_a_comment()
    {
        var predicates = PredicateExtractor.Extract("SELECT * FROM people -- WHERE secret = 1\nWHERE id = 2");
        Assert.DoesNotContain(predicates, p => p.Column == "secret");
    }

    [Fact]
    public void Resolves_an_alias_to_its_table()
    {
        var aliases = PredicateExtractor.Aliases("SELECT * FROM public.people p");
        Assert.Equal("people", aliases["p"]);
    }
}

public class IndexAdvisorTests
{
    private static readonly SqlDialect Dialect = new PostgreSqlDialect();

    private static ObjectDetail Table(string name, IReadOnlyList<string> columns,
        IReadOnlyList<IndexInfo>? indexes = null) =>
        new(new SchemaNodeRef(SchemaNodeKind.Table, ["public", name]),
            columns.Select((c, i) => new ColumnInfo(c, "text", true, null, false, false, null, i + 1)).ToList(),
            indexes ?? [], [], [], null, null, null, null);

    private static Dictionary<string, ObjectDetail> Catalog(params ObjectDetail[] tables) =>
        tables.ToDictionary(t => t.Ref.Name, t => t, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Suggests_an_index_for_an_unindexed_equality_predicate()
    {
        var findings = IndexAdvisor.Suggest("SELECT * FROM people WHERE active = true", null,
            Catalog(Table("people", ["id", "active"])), Dialect);

        var finding = Assert.Single(findings);
        Assert.Contains("CREATE INDEX", finding.Statement);
        Assert.Contains("\"people\" (\"active\")", finding.Statement);
    }

    [Fact]
    public void Orders_equality_columns_before_range_columns()
    {
        var findings = IndexAdvisor.Suggest(
            "SELECT * FROM people WHERE created_at > '2026-01-01' AND active = true", null,
            Catalog(Table("people", ["id", "active", "created_at"])), Dialect);

        Assert.Contains("(\"active\", \"created_at\")", Assert.Single(findings).Statement);
    }

    [Fact]
    public void Stops_suggesting_once_an_index_leads_with_the_column()
    {
        var findings = IndexAdvisor.Suggest("SELECT * FROM people WHERE active = true", null,
            Catalog(Table("people", ["id", "active"],
                [new IndexInfo("ix", ["active"], false, false, null)])), Dialect);

        Assert.Empty(findings);
    }

    [Fact]
    public void Suggests_an_index_for_an_unindexed_join_column()
    {
        var findings = IndexAdvisor.Suggest(
            "SELECT * FROM people p JOIN orders o ON o.person_id = p.id", null,
            Catalog(
                Table("people", ["id"], [new IndexInfo("pk", ["id"], true, true, null)]),
                Table("orders", ["id", "person_id"])),
            Dialect);

        Assert.Contains(findings, f => f.Statement!.Contains("\"orders\" (\"person_id\")"));
    }

    [Fact]
    public void Says_why_it_suggests_the_index()
    {
        var finding = Assert.Single(IndexAdvisor.Suggest("SELECT * FROM people WHERE active = true", null,
            Catalog(Table("people", ["id", "active"])), Dialect));

        Assert.Contains("no index", finding.Detail);
    }

    [Fact]
    public void Ignores_a_table_the_plan_never_scans()
    {
        var plan = new PlanNode("Index Scan", "people", null, 10, null, null, [], []);

        var findings = IndexAdvisor.Suggest("SELECT * FROM people WHERE active = true", plan,
            Catalog(Table("people", ["id", "active"])), Dialect);

        Assert.Empty(findings);
    }
}
