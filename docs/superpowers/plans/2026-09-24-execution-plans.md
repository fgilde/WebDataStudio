# Execution plans like SSMS — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A graphical, property-rich execution plan view with `.sqlplan` export and open, full depth for SQL Server.

**Architecture:** The server parses Showplan XML into a `PlanDocument` (statements → node tree with a nested property tree, raw text kept for export). `IDbDriver.ExplainDocumentAsync` defaults to wrapping `ExplainAsync`; SQL Server overrides it. `/api/query/analyze` adds `document`, `/api/plans/open` parses an uploaded file. The browser draws the document with `@xyflow/react` + `dagre` right to left, with a property grid, and saves/opens `.sqlplan`.

**Tech Stack:** .NET 10 minimal APIs, `System.Xml.Linq`, xUnit v3; React 19, Mantine 9, `@xyflow/react` 12, `@dagrejs/dagre` 3, Vitest 4 + Testing Library.

**Spec:** `docs/superpowers/specs/2026-09-24-execution-plans-design.md`

## Global Constraints

- The new `PlanNode` fields are optional (`NodeId = null, Object = null, Properties = null`); the 15 existing constructions must compile unchanged.
- `plan` and `summary` in `/api/query/analyze` keep their shape; `document` is added.
- Export only when `document.rawFormat === "sqlplan"`; file extensions `.sqlplan` and `.xml`.
- Operator icons are Tabler icons, never Microsoft's.
- The user's reference plan (`C:\Users\User\Documents\plan.sqlplan`) is never copied into the repo.
- Comments explain *why*, in the repo's voice; UI copy is English.
- Commit messages: no Co-Authored-By or any attribution lines.

## Review Focus

- A batch whose first statement has no plan (`DECLARE`, `SET`) followed by a `SELECT`: the picker lists only statements with a plan and the view opens on the first of them.
- A 6 MB / 450-node plan: parse under a second, graph renders without freezing (only visible nodes).
- A file that is not Showplan XML (a CSV, an empty file, broken XML): a 400 with a sentence, never a 502 or a stack trace.
- An estimated plan (no `RunTimeInformation`): no "actual" numbers anywhere, edges sized by estimated rows.
- A `RelOp` nested inside a scalar subquery (`ScalarOperator/Subquery/RelOp`): it appears as a child in the graph, not lost.

---

### Task 1: Plan model and Showplan parser

**Files:**
- Modify: `src/WebDataStudio.Server/Drivers/Abstractions/ResultModel.cs:36-43`
- Create: `src/WebDataStudio.Server/Drivers/SqlServer/ShowplanParser.cs`
- Test: `tests/WebDataStudio.Server.Tests/Analysis/ShowplanParserTests.cs`

**Interfaces:**
- Produces: `PlanProperty(string Name, string? Value, IReadOnlyList<PlanProperty> Children)`, `PlanStatement(string? Text, string? Type, double? Cost, IReadOnlyList<PlanProperty> Properties, IReadOnlyList<string> Warnings, IReadOnlyList<string> MissingIndexes, PlanNode? Root)`, `PlanDocument(string Engine, string? Raw, string? RawFormat, IReadOnlyList<PlanStatement> Statements)` with `static PlanDocument Single(string engine, PlanNode root)`, `PlanNode(..., int? NodeId = null, string? Object = null, IReadOnlyList<PlanProperty>? Properties = null)`, `ShowplanParser.IsShowplan(string text)`, `ShowplanParser.Parse(string xml) : PlanDocument`.

- [ ] **Step 1: Extend the model** — in `ResultModel.cs` replace the `PlanNode` record and add the new records:

```csharp
public sealed record PlanNode(
    string Operation,
    string? Detail,
    double? EstimatedCost,
    double? EstimatedRows,
    double? ActualRows,
    double? ActualMs,
    IReadOnlyList<PlanNode> Children,
    IReadOnlyList<string> Warnings,
    /// The engine's own id for the node, which is what SSMS shows and what people quote.
    int? NodeId = null,
    /// What the node reads or writes: [schema].[table].[index].
    string? Object = null,
    /// Everything else the engine said about the node, as the tree a property grid shows.
    IReadOnlyList<PlanProperty>? Properties = null);

/// One row of a property grid: a value, a group of rows, or both.
public sealed record PlanProperty(string Name, string? Value, IReadOnlyList<PlanProperty> Children);

public sealed record PlanStatement(
    string? Text,
    string? Type,
    double? Cost,
    IReadOnlyList<PlanProperty> Properties,
    IReadOnlyList<string> Warnings,
    /// Ready to run: CREATE INDEX statements with the server's estimated impact as a comment.
    IReadOnlyList<string> MissingIndexes,
    PlanNode? Root);

/// A plan as the engine wrote it down: every statement of the batch, and the original text so it
/// can be saved in the engine's own format and opened by its own tools.
public sealed record PlanDocument(
    string Engine,
    string? Raw,
    /// "sqlplan" for SQL Server; null where there is nothing worth exporting.
    string? RawFormat,
    IReadOnlyList<PlanStatement> Statements)
{
    /// The shape every engine without a richer plan answers with: one statement, its tree.
    public static PlanDocument Single(string engine, PlanNode root) =>
        new(engine, null, null, [new PlanStatement(null, null, root.EstimatedCost, [], [], [], root)]);
}
```

- [ ] **Step 2: Write the failing tests** — `tests/WebDataStudio.Server.Tests/Analysis/ShowplanParserTests.cs`:

```csharp
using WebDataStudio.Server.Drivers.SqlServer;

namespace WebDataStudio.Server.Tests.Analysis;

public class ShowplanParserTests
{
    /// Two statements (a DECLARE without a plan, then a SELECT), a nested loop over a seek and a
    /// scan, runtime counters on two threads, a missing index, a statement warning, and a RelOp
    /// hidden inside a scalar subquery.
    private const string Plan = """
        <?xml version="1.0" encoding="utf-16"?>
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6" Build="16.0">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="DECLARE @x int" StatementId="1" StatementType="DECLARE" />
            <StmtSimple StatementText="SELECT o.Id FROM dbo.Orders o JOIN dbo.Lines l ON l.OrderId = o.Id" StatementId="2" StatementType="SELECT" StatementSubTreeCost="1.5">
              <QueryPlan DegreeOfParallelism="2" MemoryGrant="1024" CompileTime="12">
                <Warnings><PlanAffectingConvert ConvertIssue="Cardinality Estimate" Expression="CONVERT(int,[l].[Code])" /></Warnings>
                <MissingIndexes>
                  <MissingIndexGroup Impact="87.5">
                    <MissingIndex Database="[shop]" Schema="[dbo]" Table="[Lines]">
                      <ColumnGroup Usage="EQUALITY"><Column Name="[OrderId]" ColumnId="2" /></ColumnGroup>
                      <ColumnGroup Usage="INCLUDE"><Column Name="[Qty]" ColumnId="3" /></ColumnGroup>
                    </MissingIndex>
                  </MissingIndexGroup>
                </MissingIndexes>
                <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10" EstimatedTotalSubtreeCost="1.5">
                  <OutputList><ColumnReference Database="[shop]" Schema="[dbo]" Table="[Orders]" Alias="[o]" Column="Id" /></OutputList>
                  <RunTimeInformation>
                    <RunTimeCountersPerThread Thread="1" ActualRows="4" ActualElapsedms="30" ActualExecutions="1" />
                    <RunTimeCountersPerThread Thread="2" ActualRows="6" ActualElapsedms="42" ActualExecutions="1" />
                  </RunTimeInformation>
                  <NestedLoops Optimized="false">
                    <RelOp NodeId="1" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="0.2">
                      <OutputList />
                      <IndexScan Ordered="true">
                        <Object Database="[shop]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" Alias="[o]" />
                        <SeekPredicates><SeekPredicateNew><SeekKeys><Prefix ScanType="EQ">
                          <RangeColumns><ColumnReference Table="[Orders]" Column="Id" /></RangeColumns>
                          <RangeExpressions><ScalarOperator ScalarString="[l].[OrderId]" /></RangeExpressions>
                        </Prefix></SeekKeys></SeekPredicateNew></SeekPredicates>
                      </IndexScan>
                    </RelOp>
                    <RelOp NodeId="2" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="5000" EstimatedTotalSubtreeCost="1.1">
                      <OutputList />
                      <Warnings NoJoinPredicate="true" />
                      <TableScan>
                        <Object Database="[shop]" Schema="[dbo]" Table="[Lines]" Alias="[l]" />
                        <Predicate><ScalarOperator ScalarString="[l].[Qty]&gt;(0)">
                          <Subquery Operation="EXISTS"><RelOp NodeId="3" PhysicalOp="Constant Scan" LogicalOp="Constant Scan" EstimateRows="1" EstimatedTotalSubtreeCost="0">
                            <OutputList /><ConstantScan />
                          </RelOp></Subquery>
                        </ScalarOperator></Predicate>
                      </TableScan>
                    </RelOp>
                  </NestedLoops>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void Recognises_showplan_and_nothing_else()
    {
        Assert.True(ShowplanParser.IsShowplan(Plan));
        Assert.False(ShowplanParser.IsShowplan("id,name\n1,ada"));
        Assert.False(ShowplanParser.IsShowplan("<root/>"));
        Assert.False(ShowplanParser.IsShowplan(""));
    }

    [Fact]
    public void Keeps_the_raw_text_for_export()
    {
        var document = ShowplanParser.Parse(Plan);

        Assert.Equal("sqlserver", document.Engine);
        Assert.Equal("sqlplan", document.RawFormat);
        Assert.Equal(Plan, document.Raw);
    }

    [Fact]
    public void Lists_only_statements_that_have_a_plan()
    {
        var statement = Assert.Single(ShowplanParser.Parse(Plan).Statements);

        Assert.Equal("SELECT", statement.Type);
        Assert.Equal(1.5, statement.Cost);
        Assert.StartsWith("SELECT o.Id", statement.Text);
    }

    [Fact]
    public void Builds_the_tree_including_a_relop_inside_a_subquery()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;

        Assert.Equal("Nested Loops", root.Operation);
        Assert.Equal("Inner Join", root.Detail);
        Assert.Equal(0, root.NodeId);
        Assert.Equal(["Clustered Index Seek", "Table Scan"], root.Children.Select(c => c.Operation));
        Assert.Equal("Constant Scan", Assert.Single(root.Children[1].Children).Operation);
    }

    [Fact]
    public void Sums_actual_rows_over_threads_and_takes_the_slowest_thread()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;

        Assert.Equal(10, root.ActualRows);
        Assert.Equal(42, root.ActualMs);
        // An estimated-only node says nothing about actuals.
        Assert.Null(root.Children[0].ActualRows);
        Assert.Null(root.Children[0].ActualMs);
    }

    [Fact]
    public void Names_the_object_a_node_touches()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;

        Assert.Equal("[dbo].[Orders].[PK_Orders] [o]", root.Children[0].Object);
        Assert.Equal("[dbo].[Lines] [l]", root.Children[1].Object);
    }

    [Fact]
    public void Carries_every_detail_as_properties()
    {
        var seek = ShowplanParser.Parse(Plan).Statements[0].Root!.Children[0];
        var properties = seek.Properties!;

        Assert.Contains(properties, p => p.Name == "Estimate Rows" && p.Value == "1");
        var indexScan = Assert.Single(properties, p => p.Name == "Index Scan");
        Assert.Contains(indexScan.Children, p => p.Name == "Ordered" && p.Value == "true");
        // Scalar operators read as the expression, not as an empty group.
        Assert.Contains("[l].[OrderId]", Flatten(indexScan).Select(p => p.Value));
        // Column references read as [table].[column].
        Assert.Contains("[Orders].[Id]", Flatten(indexScan).Select(p => p.Value));
    }

    [Fact]
    public void Shows_runtime_counters_per_thread()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;
        var runtime = Assert.Single(root.Properties!, p => p.Name == "Run Time Information");

        Assert.Equal(["Thread 1", "Thread 2"], runtime.Children.Select(c => c.Name));
    }

    [Fact]
    public void Output_list_reads_as_its_columns()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;

        Assert.Contains(root.Properties!, p => p.Name == "Output List" && p.Value == "[o].[Id]");
    }

    [Fact]
    public void Reports_warnings_of_the_statement_and_of_nodes()
    {
        var statement = ShowplanParser.Parse(Plan).Statements[0];

        Assert.Contains(statement.Warnings, w => w.Contains("Plan Affecting Convert") && w.Contains("CONVERT(int,[l].[Code])"));
        Assert.Contains(statement.Root!.Children[1].Warnings, w => w.Contains("No Join Predicate"));
    }

    [Fact]
    public void Turns_a_missing_index_into_a_statement()
    {
        var index = Assert.Single(ShowplanParser.Parse(Plan).Statements[0].MissingIndexes);

        Assert.Contains("-- estimated impact 87.5%", index);
        Assert.Contains("CREATE INDEX [IX_Lines_OrderId] ON [dbo].[Lines] ([OrderId]) INCLUDE ([Qty]);", index);
    }

    [Fact]
    public void Statement_properties_include_the_query_plan()
    {
        var properties = ShowplanParser.Parse(Plan).Statements[0].Properties;

        Assert.Contains(properties, p => p.Name == "Memory Grant" && p.Value == "1024");
        Assert.Contains(properties, p => p.Name == "Degree Of Parallelism" && p.Value == "2");
        Assert.DoesNotContain(properties, p => p.Name == "Statement Text");
    }

    [Fact]
    public void Broken_xml_is_a_format_exception()
    {
        Assert.Throws<FormatException>(() => ShowplanParser.Parse("<ShowPlanXML"));
    }

    private static IEnumerable<WebDataStudio.Server.Drivers.Abstractions.PlanProperty> Flatten(
        WebDataStudio.Server.Drivers.Abstractions.PlanProperty property) =>
        new[] { property }.Concat(property.Children.SelectMany(Flatten));
}
```

- [ ] **Step 3: Run to see it fail**

Run: `dotnet test tests/WebDataStudio.Server.Tests --filter "FullyQualifiedName~ShowplanParserTests"`
Expected: build error, `ShowplanParser` does not exist.

- [ ] **Step 4: Implement** — `src/WebDataStudio.Server/Drivers/SqlServer/ShowplanParser.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.SqlServer;

/// SQL Server's Showplan XML, read whole: every statement of the batch, every operator, and every
/// attribute and element the server wrote about them, as the tree SSMS's property grid shows.
///
/// Generic on purpose. The schema has a few hundred element types and grows with every release; a
/// parser that knew them by name would drop whatever the next version adds. Only the handful that
/// read badly as a raw tree get a rendering of their own: column references, scalar operators,
/// objects, per-thread counters and the output list.
public static partial class ShowplanParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static bool IsShowplan(string text)
    {
        // The root element is enough, and cheaper than parsing six megabytes to find out.
        var head = text.Length > 4096 ? text[..4096] : text;
        return head.Contains("<ShowPlanXML", StringComparison.Ordinal)
               && head.Contains(Ns.NamespaceName, StringComparison.Ordinal);
    }

    public static PlanDocument Parse(string xml)
    {
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (XmlException e) { throw new FormatException($"this is not readable XML: {e.Message}", e); }

        var statements = document.Descendants()
            .Where(e => e.Name.Namespace == Ns && e.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal))
            .Select(Statement)
            .OfType<PlanStatement>()
            .ToList();

        return new PlanDocument("sqlserver", xml, "sqlplan", statements);
    }

    private static PlanStatement? Statement(XElement statement)
    {
        // A conditional keeps its plan under Condition; a DECLARE or SET has none and is not shown.
        var plan = statement.Element(Ns + "QueryPlan")
                   ?? statement.Element(Ns + "Condition")?.Element(Ns + "QueryPlan");
        if (plan is null) return null;

        var properties = Attributes(statement, "StatementText")
            .Concat(Attributes(plan))
            .Concat(plan.Elements().Where(e => e.Name != Ns + "RelOp").Select(Property))
            .ToList();

        return new PlanStatement(
            (string?)statement.Attribute("StatementText"),
            (string?)statement.Attribute("StatementType"),
            Number(statement.Attribute("StatementSubTreeCost")),
            properties,
            Warnings(plan.Element(Ns + "Warnings")),
            MissingIndexes(plan),
            plan.Element(Ns + "RelOp") is { } root ? Node(root) : null);
    }

    private static PlanNode Node(XElement relOp)
    {
        var threads = relOp.Element(Ns + "RunTimeInformation")?.Elements(Ns + "RunTimeCountersPerThread").ToList() ?? [];
        var operation = (string?)relOp.Attribute("PhysicalOp") ?? "RelOp";
        var estimatedRows = Number(relOp.Attribute("EstimateRows"));

        var warnings = Warnings(relOp.Element(Ns + "Warnings")).ToList();
        if (operation.Contains("Scan", StringComparison.OrdinalIgnoreCase) && estimatedRows > 1000)
            warnings.Add("scan over many rows");

        return new PlanNode(
            operation,
            (string?)relOp.Attribute("LogicalOp"),
            Number(relOp.Attribute("EstimatedTotalSubtreeCost")),
            estimatedRows,
            // Threads each count their own rows, and run side by side: rows add up, time does not.
            threads.Count == 0 ? null : threads.Sum(t => Number(t.Attribute("ActualRows")) ?? 0),
            threads.Count == 0 ? null : threads.Max(t => Number(t.Attribute("ActualElapsedms"))),
            ChildOperators(relOp).Select(Node).ToList(),
            warnings,
            (int?)relOp.Attribute("NodeId"),
            ObjectOf(relOp),
            Attributes(relOp).Concat(relOp.Elements().Where(e => e.Name != Ns + "RelOp").Select(Property)).ToList());
    }

    /// The operators directly below this one: under its operator element, and under a subquery in
    /// one of its predicates, which SSMS draws as a child too.
    private static IEnumerable<XElement> ChildOperators(XElement relOp)
    {
        foreach (var element in relOp.Elements())
            foreach (var child in Below(element))
                yield return child;

        static IEnumerable<XElement> Below(XElement element)
        {
            if (element.Name == Ns + "RelOp") { yield return element; yield break; }
            foreach (var inner in element.Elements())
                foreach (var found in Below(inner))
                    yield return found;
        }
    }

    private static string? ObjectOf(XElement relOp)
    {
        // The operator element (IndexScan, TableScan, Update, …) holds the Object it touches.
        var target = relOp.Elements().Where(e => e.Name != Ns + "RelOp")
            .Select(e => e.Element(Ns + "Object")).FirstOrDefault(o => o is not null);
        return target is null ? null : ObjectText(target);
    }

    private static string ObjectText(XElement o)
    {
        var name = string.Join(".", new[] { "Schema", "Table", "Index" }
            .Select(a => (string?)o.Attribute(a)).Where(v => !string.IsNullOrEmpty(v)));
        return o.Attribute("Alias") is { } alias ? $"{name} {alias.Value}" : name;
    }

    private static PlanProperty Property(XElement element)
    {
        var local = element.Name.LocalName;

        switch (local)
        {
            case "ColumnReference":
                return new PlanProperty("Column", ColumnText(element), Attributes(element).ToList());
            case "Object":
                return new PlanProperty("Object", ObjectText(element), Attributes(element).ToList());
            case "ScalarOperator" when element.Attribute("ScalarString") is { } scalar:
                return new PlanProperty("Scalar Operator", scalar.Value,
                    element.Elements().Where(e => e.Name != Ns + "RelOp").Select(Property).ToList());
            case "RunTimeCountersPerThread":
                return new PlanProperty($"Thread {(string?)element.Attribute("Thread") ?? "?"}", null,
                    Attributes(element, "Thread").ToList());
        }

        var children = Attributes(element)
            .Concat(element.Elements().Where(e => e.Name != Ns + "RelOp").Select(Property))
            .ToList();

        // A list of columns (OutputList, OrderBy, GroupBy, …) reads as the columns themselves.
        var columns = element.Elements(Ns + "ColumnReference").ToList();
        if (columns.Count > 0 && columns.Count == element.Elements().Count() && !element.HasAttributes)
            return new PlanProperty(Humanize(local), string.Join(", ", columns.Select(ColumnText)), children);

        // A wrapper around one value (Predicate > ScalarOperator) reads as that value.
        if (!element.HasAttributes && children.Count == 1 && children[0].Value is { } only)
            return new PlanProperty(Humanize(local), only, children[0].Children);

        return new PlanProperty(Humanize(local), null, children);
    }

    private static string ColumnText(XElement column)
    {
        var owner = (string?)column.Attribute("Alias") ?? (string?)column.Attribute("Table");
        var name = (string?)column.Attribute("Column") ?? "?";
        if (!name.StartsWith('[')) name = $"[{name}]";
        return owner is null ? name : $"{owner}.{name}";
    }

    private static IEnumerable<PlanProperty> Attributes(XElement element, params string[] except) =>
        element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration && !except.Contains(a.Name.LocalName))
            .Select(a => new PlanProperty(Humanize(a.Name.LocalName), a.Value, []));

    private static IReadOnlyList<string> Warnings(XElement? warnings)
    {
        if (warnings is null) return [];

        // Flags on the element itself (NoJoinPredicate="true"), and one child per richer warning.
        var flags = warnings.Attributes()
            .Where(a => a.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
            .Select(a => Humanize(a.Name.LocalName));
        var details = warnings.Elements().Select(w =>
        {
            var parts = w.Attributes().Select(a => $"{Humanize(a.Name.LocalName)}={a.Value}").ToList();
            return parts.Count == 0 ? Humanize(w.Name.LocalName) : $"{Humanize(w.Name.LocalName)}: {string.Join(", ", parts)}";
        });
        return flags.Concat(details).ToList();
    }

    private static IReadOnlyList<string> MissingIndexes(XElement plan) =>
        plan.Element(Ns + "MissingIndexes")?.Elements(Ns + "MissingIndexGroup").SelectMany(group =>
            group.Elements(Ns + "MissingIndex").Select(index =>
            {
                string[] Columns(string usage) => index.Elements(Ns + "ColumnGroup")
                    .Where(g => (string?)g.Attribute("Usage") == usage)
                    .SelectMany(g => g.Elements(Ns + "Column"))
                    .Select(c => (string?)c.Attribute("Name") ?? "").ToArray();

                var keys = Columns("EQUALITY").Concat(Columns("INEQUALITY")).ToArray();
                var include = Columns("INCLUDE");
                var table = (string?)index.Attribute("Table") ?? "[?]";
                var name = $"[IX_{table.Trim('[', ']')}_{string.Join("_", keys.Select(k => k.Trim('[', ']')))}]";

                var ddl = new StringBuilder();
                if ((string?)group.Attribute("Impact") is { } impact)
                    ddl.Append("-- estimated impact ").Append(impact).Append("%\n");
                ddl.Append($"CREATE INDEX {name} ON {(string?)index.Attribute("Schema")}.{table} ({string.Join(", ", keys)})");
                if (include.Length > 0) ddl.Append($" INCLUDE ({string.Join(", ", include)})");
                return ddl.Append(';').ToString();
            })).ToList() ?? [];

    private static double? Number(XAttribute? attribute) =>
        attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// EstimatedTotalSubtreeCost → Estimated Total Subtree Cost; CPU and IO stay one word.
    private static string Humanize(string name) => CamelBreak().Replace(name, " ");

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBreak();
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/WebDataStudio.Server.Tests --filter "FullyQualifiedName~ShowplanParserTests"`
Expected: all PASS. Then the full suite (`dotnet test tests/WebDataStudio.Server.Tests`) still passes: the model change is additive.

- [ ] **Step 6: Check against the reference plan (local only, nothing committed)**

Run a throwaway console check in the scratchpad — e.g. a test run with an env var:
`$env:WDS_REFERENCE_PLAN="C:\Users\User\Documents\plan.sqlplan"` and a temporary `[Fact]` that parses it, asserts one statement, 450 nodes, and elapsed < 1 s; delete the temporary test afterwards.

- [ ] **Step 7: Commit**

```bash
git add src/WebDataStudio.Server/Drivers/Abstractions/ResultModel.cs src/WebDataStudio.Server/Drivers/SqlServer/ShowplanParser.cs tests/WebDataStudio.Server.Tests/Analysis/ShowplanParserTests.cs
git commit -m "feat(plan): Showplan-XML ganz lesen – Statements, Properties, Laufzeit pro Thread, Missing Indexes"
```

---

### Task 2: Plan documents through the drivers and `/api/query/analyze`

**Files:**
- Modify: `src/WebDataStudio.Server/Drivers/Abstractions/IDbDriver.cs:57`
- Modify: `src/WebDataStudio.Server/Drivers/Abstractions/AdoDriverBase.cs:40-41`
- Modify: `src/WebDataStudio.Server/Drivers/SqlServer/SqlServerDriver.cs:279-332`
- Create: `src/WebDataStudio.Server/Analysis/PlanFindings.cs`
- Modify: `src/WebDataStudio.Server/Endpoints/AnalysisEndpoints.cs:20-52`
- Test: `tests/WebDataStudio.Server.Tests/Analysis/PlanFindingsTests.cs`, existing `QueryEndpointTests`

**Interfaces:**
- Consumes: `ShowplanParser.Parse`, `PlanDocument.Single`.
- Produces: `IDbDriver.ExplainDocumentAsync(IDbSession, string, PlanMode, CancellationToken) : Task<PlanDocument>`; `PlanFindings.From(PlanDocument) : IEnumerable<AnalyzeFinding>`; analyze response `{ plan, document, summary, planError, findings }`.

- [ ] **Step 1: Failing test for the findings** — `tests/WebDataStudio.Server.Tests/Analysis/PlanFindingsTests.cs`:

```csharp
using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Analysis;

public class PlanFindingsTests
{
    [Fact]
    public void Missing_indexes_and_plan_warnings_become_findings()
    {
        var root = new PlanNode("Table Scan", null, 1, 50_000, null, null, [], [], Object: "[dbo].[Lines]");
        var document = new PlanDocument("sqlserver", "<x/>", "sqlplan",
        [
            new PlanStatement("SELECT 1", "SELECT", 1, [], ["Plan Affecting Convert: Expression=CONVERT(int,[x])"],
                ["-- estimated impact 90%\nCREATE INDEX [IX_Lines_A] ON [dbo].[Lines] ([A]);"], root),
        ]);

        var findings = PlanFindings.From(document).ToList();

        Assert.Contains(findings, f => f.Category == "missing-index" && f.Statement!.Contains("CREATE INDEX [IX_Lines_A]"));
        Assert.Contains(findings, f => f.Category == "plan" && f.Detail.Contains("CONVERT(int,[x])"));
    }

    [Fact]
    public void A_document_without_statements_has_no_findings()
    {
        Assert.Empty(PlanFindings.From(new PlanDocument("sqlserver", null, null, [])));
    }
}
```

Run: `dotnet test tests/WebDataStudio.Server.Tests --filter "FullyQualifiedName~PlanFindingsTests"` — Expected: build error, `PlanFindings` missing.

- [ ] **Step 2: Implement `PlanFindings`** — `src/WebDataStudio.Server/Analysis/PlanFindings.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Analysis;

/// What a whole plan document says is wrong: the rules over each statement's tree, plus what the
/// engine itself reported — its missing indexes and its warnings — which no rule has to guess.
public static class PlanFindings
{
    public static IEnumerable<AnalyzeFinding> From(PlanDocument document) =>
        document.Statements.SelectMany(statement =>
            (statement.Root is { } root ? PlanRules.Evaluate(root) : [])
            .Concat(statement.MissingIndexes.Select(ddl => new AnalyzeFinding(
                "missing-index", "warning", "The server asks for an index",
                "SQL Server noted this index while it compiled the plan. It weighs one statement, not the workload: try it before you keep it.",
                ddl)))
            .Concat(statement.Warnings.Select(warning => new AnalyzeFinding(
                "plan", "warning", "Plan warning", warning, null))));
}
```

Run the test again — Expected: PASS.

- [ ] **Step 3: The driver method** — in `IDbDriver.cs`, below `ExplainAsync`:

```csharp
    /// The plan as a document: every statement, and the engine's own text when it has a format
    /// worth saving. Engines without one answer with their tree as a single statement.
    async Task<PlanDocument> ExplainDocumentAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct) =>
        PlanDocument.Single(Info.Id, await ExplainAsync(session, sql, mode, ct));
```

In `AdoDriverBase.cs`, below `ExplainAsync` (a class-level virtual, so SQL Server can override it — a derived class cannot override a default interface method):

```csharp
    public virtual async Task<PlanDocument> ExplainDocumentAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct) =>
        PlanDocument.Single(Info.Id, await ExplainAsync(session, sql, mode, ct));
```

- [ ] **Step 4: SQL Server** — replace `ExplainAsync` in `SqlServerDriver.cs` (lines 279-332) with:

```csharp
    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct) =>
        (await ExplainDocumentAsync(session, sql, mode, ct)).Statements.FirstOrDefault(s => s.Root is not null)?.Root
        ?? new PlanNode("Plan", null, null, null, null, null, [], []);

    public override async Task<PlanDocument> ExplainDocumentAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        // SHOWPLAN_XML returns the estimated plan without executing; STATISTICS XML executes and
        // returns the actual plan as an extra result set.
        var toggle = mode == PlanMode.Actual ? "STATISTICS XML" : "SHOWPLAN_XML";

        await using (var on = session.Connection.CreateCommand())
        {
            on.CommandText = $"SET {toggle} ON";
            await on.ExecuteNonQueryAsync(ct);
        }

        string? xml;
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            xml = await ReadPlanXmlAsync(reader, ct);
        }
        finally
        {
            await using var off = session.Connection.CreateCommand();
            off.CommandText = $"SET {toggle} OFF";
            await off.ExecuteNonQueryAsync(ct);
        }

        if (xml is null) throw new InvalidOperationException("the server returned no execution plan");
        return ShowplanParser.Parse(xml);
    }
```

Remove the now unused `using System.Xml.Linq;` only if nothing else in the file uses it (`grep -n "XDocument\|XElement\|XNamespace" SqlServerDriver.cs`).

- [ ] **Step 5: The endpoint** — in `AnalysisEndpoints.cs`, `/api/query/analyze`: replace `PlanNode? plan = null;` block with

```csharp
                    PlanDocument? document = null;
                    string? planError = null;

                    if (driver.Caps.EstimatedPlan)
                    {
                        try
                        {
                            var mode = body.Actual == true && driver.Caps.ActualPlan
                                ? PlanMode.Actual : PlanMode.Estimated;
                            document = await driver.ExplainDocumentAsync(session, body.Sql, mode, ct);
                        }
                        catch (Exception e)
                        {
                            // A plan the engine refuses is worth reporting, but the SQL-only advice
                            // below still works without it.
                            planError = e.Message;
                        }
                    }

                    // The tree the rules and the old views read: the first statement that has one.
                    var plan = document?.Statements.FirstOrDefault(s => s.Root is not null)?.Root;

                    var tables = await LoadTablesAsync(driver, session, body.Sql, ct);
                    var findings = new List<AnalyzeFinding>();

                    if (document is not null) findings.AddRange(PlanFindings.From(document));
                    findings.AddRange(IndexAdvisor.Suggest(body.Sql, plan, tables, driver.Dialect));

                    return Results.Ok(new
                    {
                        plan,
                        document,
                        summary = plan is null ? null : PlanSummaryBuilder.Summarize(plan),
                        planError,
                        findings = Deduplicate(findings),
                    });
```

`PlanFindings.From` runs `PlanRules.Evaluate` over every statement, so the old single `PlanRules.Evaluate(plan)` line goes.

- [ ] **Step 6: Endpoint test** — add to `tests/WebDataStudio.Server.Tests/QueryEndpointTests.cs` (the SQLite `demo` connection there has an estimated plan):

```csharp
    [Fact]
    public async Task Analyze_returns_the_plan_as_a_document_too()
    {
        await using var factory = Factory();
        var client = factory.CreateClient();
        var id = (await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("id").GetString();

        var response = await client.PostAsJsonAsync("/api/query/analyze",
            new { connectionId = id, sql = "SELECT * FROM people WHERE name = 'ada'" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var statement = body.GetProperty("document").GetProperty("statements")[0];
        Assert.Equal(body.GetProperty("plan").GetProperty("operation").GetString(),
            statement.GetProperty("root").GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("document").GetProperty("rawFormat").ValueKind);
    }
```

Run: `dotnet test tests/WebDataStudio.Server.Tests --filter "FullyQualifiedName~QueryEndpointTests|FullyQualifiedName~PlanFindingsTests|FullyQualifiedName~IndexTrialTests|FullyQualifiedName~McpTests"` — Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/WebDataStudio.Server tests/WebDataStudio.Server.Tests
git commit -m "feat(plan): Pläne als Dokument durch Treiber und /api/query/analyze, SQL Server liefert das ganze XML"
```

---

### Task 3: `POST /api/plans/open`

**Files:**
- Modify: `src/WebDataStudio.Server/Endpoints/AnalysisEndpoints.cs` (new route inside `MapAnalysisEndpoints`)
- Test: `tests/WebDataStudio.Server.Tests/PlanOpenEndpointTests.cs`

**Interfaces:**
- Consumes: `ShowplanParser.IsShowplan/Parse`, `PlanFindings.From`, `PlanSummaryBuilder.Summarize`.
- Produces: `POST /api/plans/open` body `{ text: string }` → `200 { document, plan, summary, planError: null, findings }` | `400 { message }`.

- [ ] **Step 1: Failing test** — `tests/WebDataStudio.Server.Tests/PlanOpenEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

public class PlanOpenEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-plan").FullName;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    private const string Plan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="0.1">
              <QueryPlan><RelOp NodeId="0" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="90000" EstimatedTotalSubtreeCost="0.1">
                <OutputList /><TableScan><Object Schema="[dbo]" Table="[Big]" /></TableScan>
              </RelOp></QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public async Task Opens_a_showplan_without_a_connection()
    {
        await using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/plans/open", new { text = Plan });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sqlplan", body.GetProperty("document").GetProperty("rawFormat").GetString());
        Assert.Equal("Table Scan", body.GetProperty("plan").GetProperty("operation").GetString());
        Assert.True(body.GetProperty("findings").GetArrayLength() > 0);
    }

    [Theory]
    [InlineData("id,name\n1,ada")]
    [InlineData("")]
    [InlineData("<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"")]
    public async Task Refuses_what_is_not_a_plan_with_a_sentence(string text)
    {
        await using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/plans/open", new { text });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }
}
```

Run: `dotnet test tests/WebDataStudio.Server.Tests --filter "FullyQualifiedName~PlanOpenEndpointTests"` — Expected: FAIL (404 from the missing route).

- [ ] **Step 2: Implement** — in `MapAnalysisEndpoints`, after `/api/query/analyze`, plus the request record next to `AnalyzeQueryRequest`:

```csharp
    public record OpenPlanRequest(string? Text);
```

```csharp
        // A plan somebody saved — from SSMS, from Rider, from this studio. No connection: the file
        // is the whole of it, and the findings read the file.
        app.MapPost("/api/plans/open", (OpenPlanRequest body) =>
        {
            var text = body.Text ?? "";
            if (!ShowplanParser.IsShowplan(text))
                return Results.BadRequest(new { message = "this is not an execution plan this studio reads — it opens SQL Server plans (.sqlplan, or the XML SSMS saves)" });

            PlanDocument document;
            try { document = ShowplanParser.Parse(text); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }

            var plan = document.Statements.FirstOrDefault(s => s.Root is not null)?.Root;
            return Results.Ok(new
            {
                plan,
                document,
                summary = plan is null ? null : PlanSummaryBuilder.Summarize(plan),
                planError = (string?)null,
                findings = Deduplicate(PlanFindings.From(document)),
            });
        });
```

Add `using WebDataStudio.Server.Drivers.SqlServer;` at the top. Check the auth middleware allows it like other `/api` routes (it sits behind the same login gate — correct, nothing to change).

- [ ] **Step 3: Run the tests** — same filter, Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/WebDataStudio.Server/Endpoints/AnalysisEndpoints.cs tests/WebDataStudio.Server.Tests/PlanOpenEndpointTests.cs
git commit -m "feat(plan): /api/plans/open liest gespeicherte .sqlplan-Dateien ohne Connection"
```

---

### Task 4: Browser types, API call and the plan model helpers

**Files:**
- Modify: `web/src/api.ts:746-790`
- Create: `web/src/plan/planModel.ts`
- Test: `web/src/plan/planModel.test.ts`

**Interfaces:**
- Produces (api.ts): `PlanPropertyDto { name: string; value: string | null; children: PlanPropertyDto[] }`; `PlanNodeDto` gains `nodeId?: number | null; object?: string | null; properties?: PlanPropertyDto[] | null`; `PlanStatementDto { text: string | null; type: string | null; cost: number | null; properties: PlanPropertyDto[]; warnings: string[]; missingIndexes: string[]; root: PlanNodeDto | null }`; `PlanDocumentDto { engine: string; raw: string | null; rawFormat: string | null; statements: PlanStatementDto[] }`; `AnalyzeResultDto.document?: PlanDocumentDto | null`; `openPlan(text: string): Promise<AnalyzeResultDto>`.
- Produces (planModel.ts): `type OperatorFamily`, `operatorFamily(operation: string): OperatorFamily`, `ownCost(node: PlanNodeDto): number`, `costShare(node, statementCost): number | null`, `rowsOut(node): number | null`, `edgeWidth(rows: number | null): number`, `flattenPlan(root): { node: PlanNodeDto; id: string; parentId: string | null }[]`, `layoutPlan(root): { nodes: PositionedPlanNode[]; edges: PlanEdge[] }`, `filterProperties(props: PlanPropertyDto[], query: string): PlanPropertyDto[]`, `savePlanFile(raw: string, name: string, extension: "sqlplan" | "xml"): void`.

- [ ] **Step 1: api.ts** — replace the `PlanNodeDto` / `AnalyzeResultDto` block with:

```ts
export interface PlanPropertyDto { name: string; value: string | null; children: PlanPropertyDto[] }
export interface PlanNodeDto {
  operation: string; detail: string | null;
  estimatedCost: number | null; estimatedRows: number | null;
  actualRows: number | null; actualMs: number | null;
  children: PlanNodeDto[]; warnings: string[];
  nodeId?: number | null; object?: string | null; properties?: PlanPropertyDto[] | null;
}
export interface PlanStatementDto {
  text: string | null; type: string | null; cost: number | null;
  properties: PlanPropertyDto[]; warnings: string[]; missingIndexes: string[];
  root: PlanNodeDto | null;
}
/// A plan as the engine wrote it: every statement, and its own text when it can be saved.
export interface PlanDocumentDto {
  engine: string; raw: string | null; rawFormat: string | null; statements: PlanStatementDto[];
}
export interface FindingDto {
  category: string; severity: string; title: string; detail: string; statement: string | null;
}
export interface AnalyzeResultDto {
  plan: PlanNodeDto | null;
  document?: PlanDocumentDto | null;
  summary: { totalCost: number | null; maxNodeCost: number; nodeCount: number } | null;
  planError: string | null;
  findings: FindingDto[];
}
```

and next to `analyzeQuery`:

```ts
/// A saved plan — .sqlplan or the XML SSMS writes — read on the server, without a connection.
export const openPlan = (text: string): Promise<AnalyzeResultDto> =>
  fetch(`${base}/plans/open`, json("POST", { text })).then(r => ok<AnalyzeResultDto>(r));
```

- [ ] **Step 2: Failing tests** — `web/src/plan/planModel.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import type { PlanNodeDto } from "../api";
import {
  costShare, edgeWidth, filterProperties, flattenPlan, layoutPlan, operatorFamily, ownCost, rowsOut,
} from "./planModel";

const node = (operation: string, cost: number, children: PlanNodeDto[] = [],
  extra: Partial<PlanNodeDto> = {}): PlanNodeDto => ({
  operation, detail: null, estimatedCost: cost, estimatedRows: 10, actualRows: null, actualMs: null,
  children, warnings: [], ...extra,
});

describe("planModel", () => {
  it("groups operators into families", () => {
    expect(operatorFamily("Clustered Index Seek")).toBe("seek");
    expect(operatorFamily("Index Scan")).toBe("scan");
    expect(operatorFamily("Table Scan")).toBe("scan");
    expect(operatorFamily("Key Lookup")).toBe("lookup");
    expect(operatorFamily("Nested Loops")).toBe("join");
    expect(operatorFamily("Hash Match")).toBe("join");
    expect(operatorFamily("Stream Aggregate")).toBe("aggregate");
    expect(operatorFamily("Sort")).toBe("sort");
    expect(operatorFamily("Table Spool")).toBe("spool");
    expect(operatorFamily("Compute Scalar")).toBe("compute");
    expect(operatorFamily("Filter")).toBe("filter");
    expect(operatorFamily("Parallelism")).toBe("parallelism");
    expect(operatorFamily("Clustered Index Update")).toBe("write");
    expect(operatorFamily("Seq Scan")).toBe("scan");
    expect(operatorFamily("Something New")).toBe("other");
  });

  it("own cost is the subtree minus the children, and never negative", () => {
    const tree = node("Nested Loops", 10, [node("Index Seek", 3), node("Table Scan", 5)]);
    expect(ownCost(tree)).toBe(2);
    expect(ownCost(node("Sort", 1, [node("Scan", 2)]))).toBe(0);
    expect(costShare(tree, 10)).toBeCloseTo(0.2);
    expect(costShare(tree, null)).toBeNull();
  });

  it("rows out prefers the actual count", () => {
    expect(rowsOut(node("Scan", 1, [], { estimatedRows: 5, actualRows: 7 }))).toBe(7);
    expect(rowsOut(node("Scan", 1, [], { estimatedRows: 5 }))).toBe(5);
    expect(rowsOut(node("Scan", 1, [], { estimatedRows: null }))).toBeNull();
  });

  it("edges widen with the rows they carry", () => {
    expect(edgeWidth(null)).toBe(1);
    expect(edgeWidth(1)).toBeLessThan(edgeWidth(1000));
    expect(edgeWidth(1000)).toBeLessThan(edgeWidth(1_000_000));
    expect(edgeWidth(1e12)).toBeLessThanOrEqual(12);
  });

  it("flattens with stable ids and parents", () => {
    const rows = flattenPlan(node("Root", 1, [node("A", 0), node("B", 0, [node("C", 0)])]));
    expect(rows.map(r => [r.id, r.parentId])).toEqual([
      ["0", null], ["0.0", "0"], ["0.1", "0"], ["0.1.0", "0.1"],
    ]);
  });

  it("lays out right to left: the root is left of its children", () => {
    const { nodes, edges } = layoutPlan(node("Root", 1, [node("A", 0), node("B", 0)]));
    const x = (id: string) => nodes.find(n => n.id === id)!.position.x;
    expect(x("0")).toBeLessThan(x("0.0"));
    expect(x("0")).toBeLessThan(x("0.1"));
    // Data flows from the child into its parent.
    expect(edges.map(e => [e.source, e.target])).toEqual([["0.0", "0"], ["0.1", "0"]]);
  });

  it("filters properties by name or value and keeps the path to a match", () => {
    const props = [
      { name: "Estimate Rows", value: "10", children: [] },
      { name: "Index Scan", value: null, children: [{ name: "Object", value: "[dbo].[Orders]", children: [] }] },
    ];
    expect(filterProperties(props, "")).toEqual(props);
    expect(filterProperties(props, "orders")).toEqual([
      { name: "Index Scan", value: null, children: [{ name: "Object", value: "[dbo].[Orders]", children: [] }] },
    ]);
    expect(filterProperties(props, "estimate").map(p => p.name)).toEqual(["Estimate Rows"]);
  });
});
```

Run: `cd web && npx vitest run src/plan/planModel.test.ts` — Expected: FAIL, module not found.

- [ ] **Step 3: Implement** — `web/src/plan/planModel.ts`:

```ts
import dagre from "@dagrejs/dagre";
import type { PlanNodeDto, PlanPropertyDto } from "../api";

export type OperatorFamily =
  | "seek" | "scan" | "lookup" | "join" | "aggregate" | "sort" | "spool" | "compute" | "filter"
  | "parallelism" | "write" | "other";

/// The operator names of every engine, reduced to what an icon can say. Order matters: "Index
/// Seek" before "Index", "Clustered Index Update" before "Scan".
const FAMILIES: [RegExp, OperatorFamily][] = [
  [/insert|update|delete|merge$/i, "write"],
  [/seek/i, "seek"],
  [/lookup/i, "lookup"],
  [/scan/i, "scan"],
  [/loop|hash|merge join|join/i, "join"],
  [/aggregate|group/i, "aggregate"],
  [/sort|top n/i, "sort"],
  [/spool/i, "spool"],
  [/compute|scalar|project/i, "compute"],
  [/filter/i, "filter"],
  [/parallelism|gather|repartition|distribute/i, "parallelism"],
];

export const operatorFamily = (operation: string): OperatorFamily =>
  FAMILIES.find(([pattern]) => pattern.test(operation))?.[1] ?? "other";

/// What this operator costs by itself. The engine reports the whole subtree; SSMS's percentages
/// are this, and rounding in the plan can make it dip below zero.
export const ownCost = (node: PlanNodeDto): number =>
  Math.max(0, (node.estimatedCost ?? 0) - node.children.reduce((sum, c) => sum + (c.estimatedCost ?? 0), 0));

export const costShare = (node: PlanNodeDto, statementCost: number | null): number | null =>
  statementCost && statementCost > 0 ? ownCost(node) / statementCost : null;

/// The rows an operator hands to its parent: what happened if the plan ran, else the guess.
export const rowsOut = (node: PlanNodeDto): number | null => node.actualRows ?? node.estimatedRows ?? null;

/// Logarithmic, as in SSMS: a million rows should look heavier than a thousand, not a thousand
/// times heavier.
export const edgeWidth = (rows: number | null): number =>
  rows === null ? 1 : Math.min(12, 1 + Math.log10(Math.max(1, rows)) * 1.2);

export interface FlatPlanNode { node: PlanNodeDto; id: string; parentId: string | null }

/// Ids are paths ("0.1.0"), stable for the same plan, so a selection survives a re-render.
export function flattenPlan(root: PlanNodeDto): FlatPlanNode[] {
  const out: FlatPlanNode[] = [];
  const walk = (node: PlanNodeDto, id: string, parentId: string | null) => {
    out.push({ node, id, parentId });
    node.children.forEach((child, i) => walk(child, `${id}.${i}`, id));
  };
  walk(root, "0", null);
  return out;
}

export const NODE_WIDTH = 190;
export const NODE_HEIGHT = 96;

export interface PositionedPlanNode { id: string; node: PlanNodeDto; position: { x: number; y: number } }
export interface PlanEdge { id: string; source: string; target: string; rows: number | null }

/// Right to left, the way SSMS draws it: the statement's result on the left, the tables on the
/// right, and every arrow pointing at the operator that consumes the rows.
export function layoutPlan(root: PlanNodeDto): { nodes: PositionedPlanNode[]; edges: PlanEdge[] } {
  const flat = flattenPlan(root);
  const graph = new dagre.graphlib.Graph();
  graph.setGraph({ rankdir: "LR", nodesep: 24, ranksep: 70 });
  graph.setDefaultEdgeLabel(() => ({}));

  for (const { id } of flat) graph.setNode(id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  for (const { id, parentId } of flat) if (parentId) graph.setEdge(parentId, id);
  dagre.layout(graph);

  return {
    nodes: flat.map(({ id, node }) => {
      const { x, y } = graph.node(id);
      return { id, node, position: { x: x - NODE_WIDTH / 2, y: y - NODE_HEIGHT / 2 } };
    }),
    edges: flat.filter(f => f.parentId).map(({ id, node, parentId }) => ({
      id: `${id}->${parentId}`, source: id, target: parentId!, rows: rowsOut(node),
    })),
  };
}

/// Keeps a property when its name or value matches, or when something below it does — so the path
/// to a match stays visible, as in a property grid's search.
export function filterProperties(props: PlanPropertyDto[], query: string): PlanPropertyDto[] {
  const q = query.trim().toLowerCase();
  if (!q) return props;
  return props.flatMap(p => {
    if (p.name.toLowerCase().includes(q) || (p.value ?? "").toLowerCase().includes(q)) return [p];
    const children = filterProperties(p.children, q);
    return children.length > 0 ? [{ ...p, children }] : [];
  });
}

/// The plan in its engine's own format, so SSMS or Rider opens it as if it had written it.
export function savePlanFile(raw: string, name: string, extension: "sqlplan" | "xml") {
  const url = URL.createObjectURL(new Blob([raw], { type: "application/xml" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `${name}.${extension}`;
  link.click();
  URL.revokeObjectURL(url);
}
```

Note the dagre edge direction: the layout edge runs parent → child so `rankdir: "LR"` puts the root leftmost; the rendered edge runs child → parent.

- [ ] **Step 4: Run** — `cd web && npx vitest run src/plan/planModel.test.ts` — Expected: PASS. `npx tsc -b` — no errors.

- [ ] **Step 5: Commit**

```bash
git add web/src/api.ts web/src/plan/planModel.ts web/src/plan/planModel.test.ts
git commit -m "feat(plan): Plan-Dokument im Browser – Typen, Layout von rechts nach links, Kostenanteile, Datei speichern"
```

---

### Task 5: Graph, property grid and document view in the plan panel

**Files:**
- Create: `web/src/plan/PlanGraph.tsx`, `web/src/plan/PlanProperties.tsx`, `web/src/plan/PlanDocumentView.tsx`
- Modify: `web/src/plan/PlanPanel.tsx` (Graph tab, export, open)
- Test: `web/src/plan/PlanDocumentView.test.tsx`, `web/src/plan/PlanPanel.test.tsx`

**Interfaces:**
- Consumes: everything from Task 4.
- Produces: `PlanDocumentView({ document, onRunStatement?, name })`; `PlanPanel` gains prop `onOpenPlan?: (name: string, result: AnalyzeResultDto) => void`; `openPlanFile(file: File): Promise<AnalyzeResultDto>` exported from `PlanDocumentView.tsx`.

- [ ] **Step 1: Property grid** — `web/src/plan/PlanProperties.tsx`:

```tsx
import { useState } from "react";
import { ActionIcon, Group, ScrollArea, Stack, Text, TextInput, Tooltip, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconCopy, IconSearch } from "@tabler/icons-react";
import type { PlanPropertyDto } from "../api";
import { filterProperties } from "./planModel";

/// Everything the engine said about one operator, as SSMS's property window shows it: groups
/// that fold, a search that keeps the path to what it found, and every value copyable.
export function PlanProperties({ title, properties }: { title: string; properties: PlanPropertyDto[] }) {
  const [query, setQuery] = useState("");
  const shown = filterProperties(properties, query);

  return (
    <Stack gap={4} h="100%" style={{ minHeight: 0 }}>
      <Text size="xs" fw={700} px={6} pt={4} truncate>{title}</Text>
      <TextInput size="xs" mx={6} placeholder="Search properties" leftSection={<IconSearch size={12} />}
        value={query} onChange={e => setQuery(e.currentTarget.value)} />
      <ScrollArea style={{ flex: 1 }}>
        {shown.length === 0 && <Text size="xs" c="dimmed" p={6}>Nothing here.</Text>}
        {shown.map((p, i) => <PropertyRow key={`${p.name}-${i}`} property={p} depth={0} open={query !== ""} />)}
      </ScrollArea>
    </Stack>
  );
}

function PropertyRow({ property, depth, open }: { property: PlanPropertyDto; depth: number; open: boolean }) {
  const [expanded, setExpanded] = useState(open);
  const isOpen = expanded || open;
  const hasChildren = property.children.length > 0;

  return (
    <>
      <Group gap={4} wrap="nowrap" px={6} py={1} pl={6 + depth * 12}
        style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
        <UnstyledButton aria-label={hasChildren ? `Expand ${property.name}` : undefined}
          onClick={() => hasChildren && setExpanded(e => !e)}
          style={{ width: 12, visibility: hasChildren ? "visible" : "hidden" }}>
          {isOpen ? <IconChevronDown size={11} /> : <IconChevronRight size={11} />}
        </UnstyledButton>
        <Text size="xs" c="dimmed" w="45%" truncate title={property.name}>{property.name}</Text>
        <Text size="xs" ff="monospace" style={{ flex: 1, wordBreak: "break-all" }}>{property.value ?? ""}</Text>
        {property.value && (
          <Tooltip label="Copy">
            <ActionIcon size="xs" variant="subtle" aria-label={`Copy ${property.name}`}
              onClick={() => navigator.clipboard.writeText(property.value!)}>
              <IconCopy size={11} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>
      {isOpen && property.children.map((c, i) =>
        <PropertyRow key={`${c.name}-${i}`} property={c} depth={depth + 1} open={open} />)}
    </>
  );
}
```

- [ ] **Step 2: Graph** — `web/src/plan/PlanGraph.tsx`:

```tsx
import { memo, useEffect, useMemo } from "react";
import {
  Background, Controls, Handle, MiniMap, Position, ReactFlow, ReactFlowProvider, useReactFlow,
  type Edge, type Node, type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { Group, Text, Tooltip } from "@mantine/core";
import {
  IconAlertTriangle, IconArrowsSplit, IconBolt, IconCalculator, IconDatabaseEdit, IconFilter,
  IconFocus2, IconLayersIntersect, IconListSearch, IconSortDescending, IconStack2, IconSum,
  IconTable, IconTopologyStar,
} from "@tabler/icons-react";
import type { PlanNodeDto } from "../api";
import { heatColor } from "./heat";
import {
  NODE_HEIGHT, NODE_WIDTH, costShare, edgeWidth, layoutPlan, operatorFamily, ownCost,
  type OperatorFamily,
} from "./planModel";

const ICONS: Record<OperatorFamily, typeof IconTable> = {
  seek: IconFocus2, scan: IconTable, lookup: IconListSearch, join: IconLayersIntersect,
  aggregate: IconSum, sort: IconSortDescending, spool: IconStack2, compute: IconCalculator,
  filter: IconFilter, parallelism: IconArrowsSplit, write: IconDatabaseEdit, other: IconTopologyStar,
};

interface CardData extends Record<string, unknown> {
  node: PlanNodeDto; share: number | null; heat: string; selected: boolean; match: boolean;
}

const format = (n: number) => Math.round(n).toLocaleString();

const PlanCard = memo(function PlanCard({ data }: NodeProps<Node<CardData>>) {
  const { node, share, heat, selected, match } = data;
  const Icon = ICONS[operatorFamily(node.operation)];

  return (
    <div style={{
      width: NODE_WIDTH, height: NODE_HEIGHT, padding: 6, borderRadius: 8, overflow: "hidden",
      background: `linear-gradient(${heat}, ${heat}), var(--mantine-color-body)`,
      border: `${selected ? 2 : 1}px solid ${selected ? "var(--mantine-primary-color-filled)"
        : match ? "var(--mantine-color-yellow-5)" : "var(--mantine-color-default-border)"}`,
      boxShadow: match ? "0 0 0 3px var(--mantine-color-yellow-3)" : undefined,
    }}>
      {/* The rows arrive from the right and leave to the left. */}
      <Handle type="target" position={Position.Right} style={{ opacity: 0 }} />
      <Handle type="source" position={Position.Left} style={{ opacity: 0 }} />
      <Group gap={4} wrap="nowrap">
        <Icon size={16} />
        <Text size="xs" fw={700} truncate style={{ flex: 1 }}>{node.operation}</Text>
        {node.warnings.length > 0 && (
          <Tooltip label={node.warnings.join("; ")} withinPortal>
            <IconAlertTriangle size={13} color="var(--mantine-color-orange-6)" />
          </Tooltip>
        )}
      </Group>
      {node.object && <Text size="10px" c="dimmed" truncate title={node.object}>{node.object}</Text>}
      <Group gap={6} mt={2} wrap="nowrap">
        {share !== null && <Text size="10px" fw={700}>{Math.round(share * 100)}%</Text>}
        {node.actualMs != null && <Text size="10px" c="dimmed"><IconBolt size={9} /> {node.actualMs.toFixed(0)} ms</Text>}
      </Group>
      <Text size="10px" c="dimmed" truncate>
        {node.actualRows != null ? `${format(node.actualRows)} of ` : ""}
        {node.estimatedRows != null ? `${format(node.estimatedRows)} est.` : ""}
      </Text>
    </div>
  );
});

const nodeTypes = { plan: PlanCard };

export function PlanGraph(props: {
  root: PlanNodeDto; statementCost: number | null; selected: string | null;
  onSelect: (id: string | null, node: PlanNodeDto | null) => void; matches: Set<string>; focus: string | null;
}) {
  return <ReactFlowProvider><Graph {...props} /></ReactFlowProvider>;
}

function Graph({ root, statementCost, selected, onSelect, matches, focus }: Parameters<typeof PlanGraph>[0]) {
  const layout = useMemo(() => layoutPlan(root), [root]);
  const maxOwn = useMemo(() => Math.max(0, ...layout.nodes.map(n => ownCost(n.node))), [layout]);
  const flow = useReactFlow();

  const nodes: Node<CardData>[] = useMemo(() => layout.nodes.map(({ id, node, position }) => ({
    id, type: "plan", position, draggable: false,
    data: {
      node, share: costShare(node, statementCost), heat: heatColor(ownCost(node), maxOwn),
      selected: id === selected, match: matches.has(id),
    },
  })), [layout, statementCost, maxOwn, selected, matches]);

  const edges: Edge[] = useMemo(() => layout.edges.map(e => ({
    id: e.id, source: e.source, target: e.target, type: "smoothstep",
    label: e.rows === null ? undefined : Math.round(e.rows).toLocaleString(),
    labelStyle: { fontSize: 9 }, labelShowBg: false,
    style: { strokeWidth: edgeWidth(e.rows) },
  })), [layout]);

  // Search steps through matches: the one in focus is centred.
  useEffect(() => {
    const target = focus ? layout.nodes.find(n => n.id === focus) : null;
    if (target) flow.setCenter(target.position.x + NODE_WIDTH / 2, target.position.y + NODE_HEIGHT / 2, { zoom: 1, duration: 300 });
  }, [focus, layout, flow]);

  return (
    <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} fitView minZoom={0.05}
      onlyRenderVisibleElements nodesConnectable={false} proOptions={{ hideAttribution: true }}
      onNodeClick={(_, n) => onSelect(n.id, (n.data as CardData).node)} onPaneClick={() => onSelect(null, null)}>
      <Background gap={24} size={1} />
      <Controls showInteractive={false} />
      {layout.nodes.length > 20 && <MiniMap pannable zoomable />}
    </ReactFlow>
  );
}
```

- [ ] **Step 3: Document view** — `web/src/plan/PlanDocumentView.tsx`:

```tsx
import { useMemo, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Menu, Select, Stack, Text, TextInput, Tooltip,
} from "@mantine/core";
import { IconChevronDown, IconCopy, IconDownload, IconPlayerPlay, IconSearch } from "@tabler/icons-react";
import { openPlan, type AnalyzeResultDto, type PlanDocumentDto, type PlanNodeDto } from "../api";
import { PlanGraph } from "./PlanGraph";
import { PlanProperties } from "./PlanProperties";
import { flattenPlan, savePlanFile } from "./planModel";

/// A plan file from disk, read on the server — the one parser, so a file gets the same findings as
/// a plan fetched live.
export const openPlanFile = async (file: File): Promise<AnalyzeResultDto> => openPlan(await file.text());

const statementLabel = (text: string | null, i: number) =>
  `${i + 1}. ${(text ?? "statement").replace(/\s+/g, " ").trim().slice(0, 80)}`;

export function PlanDocumentView({ document, name, onRunStatement }: {
  document: PlanDocumentDto; name: string; onRunStatement?: (statement: string) => void;
}) {
  const withPlans = document.statements.filter(s => s.root);
  const [index, setIndex] = useState(0);
  const statement = withPlans[Math.min(index, withPlans.length - 1)];
  const [selected, setSelected] = useState<{ id: string; node: PlanNodeDto } | null>(null);
  const [search, setSearch] = useState("");
  const [cursor, setCursor] = useState(0);

  const matches = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q || !statement?.root) return [];
    return flattenPlan(statement.root)
      .filter(f => f.node.operation.toLowerCase().includes(q) || (f.node.object ?? "").toLowerCase().includes(q))
      .map(f => f.id);
  }, [search, statement]);
  const matchSet = useMemo(() => new Set(matches), [matches]);

  if (!statement?.root) return <Text size="xs" c="dimmed" p="xs">This plan has no statement with an operator tree.</Text>;

  const header = statement.properties.filter(p =>
    ["Degree Of Parallelism", "Memory Grant", "Compile Time", "Statement Optm Level", "Cardinality Estimation Model Version"]
      .includes(p.name));

  return (
    <Stack gap={4} h="100%" style={{ minHeight: 0 }}>
      <Group gap={6} px={6} wrap="nowrap">
        {withPlans.length > 1 && (
          <Select size="xs" w={320} aria-label="Statement" allowDeselect={false}
            data={withPlans.map((s, i) => ({ value: String(i), label: statementLabel(s.text, i) }))}
            value={String(index)} onChange={v => { setIndex(Number(v)); setSelected(null); }} />
        )}
        {statement.cost != null && <Badge size="sm" variant="light" tt="none">cost {statement.cost.toFixed(3)}</Badge>}
        {header.map(p => <Text key={p.name} size="10px" c="dimmed">{p.name} {p.value}</Text>)}
        <div style={{ flex: 1 }} />
        <TextInput size="xs" w={200} placeholder="Find operator or object" leftSection={<IconSearch size={12} />}
          value={search} onChange={e => { setSearch(e.currentTarget.value); setCursor(0); }}
          onKeyDown={e => { if (e.key === "Enter" && matches.length) setCursor(c => (c + 1) % matches.length); }}
          rightSection={matches.length ? <Text size="10px" c="dimmed">{cursor + 1}/{matches.length}</Text> : null}
          rightSectionWidth={44} />
        {document.rawFormat === "sqlplan" && document.raw && (
          <Menu position="bottom-end">
            <Menu.Target>
              <Button size="compact-xs" variant="default" leftSection={<IconDownload size={12} />}
                rightSection={<IconChevronDown size={10} />}>Save</Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item onClick={() => savePlanFile(document.raw!, name, "sqlplan")}>Save as .sqlplan</Menu.Item>
              <Menu.Item onClick={() => savePlanFile(document.raw!, name, "xml")}>Save as .xml</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        )}
      </Group>

      {(statement.warnings.length > 0 || statement.missingIndexes.length > 0) && (
        <Stack gap={2} px={6}>
          {statement.warnings.map((w, i) => <Text key={i} size="xs" c="orange">⚠ {w}</Text>)}
          {statement.missingIndexes.map((ddl, i) => (
            <Alert key={i} variant="light" color="teal" p={4}>
              <Group gap={4} wrap="nowrap">
                <Text size="xs" ff="monospace" style={{ flex: 1, whiteSpace: "pre-wrap" }}>{ddl}</Text>
                <Tooltip label="Copy">
                  <ActionIcon size="xs" variant="subtle" aria-label="Copy index" onClick={() => navigator.clipboard.writeText(ddl)}>
                    <IconCopy size={12} />
                  </ActionIcon>
                </Tooltip>
                {onRunStatement && (
                  <Tooltip label="Open in a new query tab">
                    <ActionIcon size="xs" variant="subtle" aria-label="Open index in a query tab" onClick={() => onRunStatement(ddl)}>
                      <IconPlayerPlay size={12} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>
            </Alert>
          ))}
        </Stack>
      )}

      <div style={{ flex: 1, minHeight: 0, display: "flex" }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <PlanGraph root={statement.root} statementCost={statement.cost} selected={selected?.id ?? null}
            onSelect={(id, node) => setSelected(id && node ? { id, node } : null)}
            matches={matchSet} focus={matches[cursor] ?? null} />
        </div>
        <div style={{ width: 340, borderLeft: "1px solid var(--mantine-color-default-border)", minHeight: 0 }}>
          <PlanProperties
            title={selected ? `${selected.node.operation}${selected.node.nodeId != null ? ` (Node ${selected.node.nodeId})` : ""}` : "Statement"}
            properties={selected ? selected.node.properties ?? [] : statement.properties} />
        </div>
      </div>
    </Stack>
  );
}
```

- [ ] **Step 4: Wire into `PlanPanel.tsx`**
  - Props: add `onOpenPlan?: (name: string, result: AnalyzeResultDto) => void;`
  - Imports: `IconFolderOpen` from tabler; `PlanDocumentView, openPlanFile` from `./PlanDocumentView`; `useRef` from react.
  - Toolbar, after the refresh `ActionIcon`:

```tsx
        {onOpenPlan && (
          <>
            <Tooltip label="Open a saved plan — .sqlplan or .xml">
              <ActionIcon size="sm" variant="subtle" aria-label="Open plan" onClick={() => fileInput.current?.click()}>
                <IconFolderOpen size={14} />
              </ActionIcon>
            </Tooltip>
            <input ref={fileInput} type="file" accept=".sqlplan,.xml" hidden aria-label="Plan file"
              onChange={e => { const f = e.currentTarget.files?.[0]; e.currentTarget.value = ""; if (f) open(f); }} />
          </>
        )}
```

  - In the component body:

```tsx
  const fileInput = useRef<HTMLInputElement>(null);
  const open = (file: File) => openPlanFile(file)
    .then(result => onOpenPlan?.(file.name.replace(/\.(sqlplan|xml)$/i, ""), result))
    .catch(e => setError(e instanceof Error ? e.message : String(e)));
```

  - The outer `div` gets drop support: `onDragOver={e => { if (onOpenPlan && e.dataTransfer.types.includes("Files")) e.preventDefault(); }}` and `onDrop={e => { const f = e.dataTransfer.files[0]; if (onOpenPlan && f) { e.preventDefault(); open(f); } }}`.
  - Tabs: `defaultValue={result.document ? "graph" : "tree"}`; first tab `{result.document && <Tabs.Tab value="graph">Graph</Tabs.Tab>}`; panel:

```tsx
          {result.document && (
            <Tabs.Panel value="graph" style={{ flex: 1, minHeight: 0 }}>
              <PlanDocumentView document={result.document} name="plan" onRunStatement={onRunStatement} />
            </Tabs.Panel>
          )}
```

- [ ] **Step 5: Tests** — `web/src/plan/PlanDocumentView.test.tsx`:

```tsx
// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import type { PlanDocumentDto } from "../api";

vi.mock("../api", () => ({ openPlan: vi.fn() }));
const save = vi.fn();
vi.mock("./planModel", async original => ({ ...(await original<typeof import("./planModel")>()), savePlanFile: (...a: unknown[]) => save(...a) }));

const { PlanDocumentView } = await import("./PlanDocumentView");

const doc = (rawFormat: string | null): PlanDocumentDto => ({
  engine: "sqlserver", raw: "<ShowPlanXML/>", rawFormat,
  statements: [
    { text: "DECLARE @x int", type: "DECLARE", cost: null, properties: [], warnings: [], missingIndexes: [], root: null },
    {
      text: "SELECT 1", type: "SELECT", cost: 1, warnings: ["Plan Affecting Convert: x"],
      missingIndexes: ["CREATE INDEX [IX] ON [dbo].[T] ([A]);"],
      properties: [{ name: "Memory Grant", value: "1024", children: [] }],
      root: { operation: "Table Scan", detail: null, estimatedCost: 1, estimatedRows: 5, actualRows: null, actualMs: null, children: [], warnings: [] },
    },
  ],
});

const draw = (d: PlanDocumentDto) => render(<MantineProvider><PlanDocumentView document={d} name="q" /></MantineProvider>);

describe("PlanDocumentView", () => {
  it("opens on the first statement that has a plan, with its header, warnings and missing index", () => {
    draw(doc("sqlplan"));
    expect(screen.getByText("Memory Grant 1024")).toBeTruthy();
    expect(screen.getByText(/Plan Affecting Convert/)).toBeTruthy();
    expect(screen.getByText("CREATE INDEX [IX] ON [dbo].[T] ([A]);")).toBeTruthy();
    // One statement with a plan: no picker.
    expect(screen.queryByLabelText("Statement")).toBeNull();
  });

  it("saves the raw plan as .sqlplan", async () => {
    draw(doc("sqlplan"));
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));
    fireEvent.click(await screen.findByText("Save as .sqlplan"));
    expect(save).toHaveBeenCalledWith("<ShowPlanXML/>", "q", "sqlplan");
  });

  it("offers no export for a plan without its own format", () => {
    draw(doc(null));
    expect(screen.queryByRole("button", { name: /Save/ })).toBeNull();
  });
});
```

Add to `web/src/plan/PlanPanel.test.tsx` (mirror its existing `vi.mock("../api", …)`; add `openPlan: (...a) => openPlanMock(...a)` to that mock):

```tsx
  it("opens a dropped .sqlplan file and hands it to the caller", async () => {
    const onOpenPlan = vi.fn();
    openPlanMock.mockResolvedValue({ plan: null, document: null, summary: null, planError: null, findings: [] });
    render(<MantineProvider><PlanPanel connectionId="c" sql="SELECT 1" onOpenPlan={onOpenPlan} /></MantineProvider>);

    const file = new File(["<ShowPlanXML/>"], "slow.sqlplan");
    fireEvent.change(screen.getByLabelText("Plan file"), { target: { files: [file] } });

    await waitFor(() => expect(onOpenPlan).toHaveBeenCalledWith("slow", expect.anything()));
    expect(openPlanMock).toHaveBeenCalledWith("<ShowPlanXML/>");
  });
```

jsdom's `File.text()` exists in the installed jsdom; if not, the test setup's polyfills are in `src/test-setup.ts`. `ResizeObserver`/`matchMedia` shims are already in `test-setup.ts` (vite.config `setupFiles`). React Flow in jsdom renders without measuring — the view tests do not assert on graph nodes, only on the header.

Run: `cd web && npx vitest run src/plan` — Expected: PASS. `npx tsc -b && npx oxlint src/plan` — clean.

- [ ] **Step 6: Commit**

```bash
git add web/src/plan
git commit -m "feat(plan): Graph wie in SSMS mit Property-Grid, Suche, Missing Indexes und .sqlplan-Export"
```

---

### Task 6: Plan files as dock tabs, palette command, docs

**Files:**
- Create: `web/src/plan/planFiles.ts`
- Modify: `web/src/dock/DockShell.tsx` (component `planFile`, `openPlanTab`, `PlanDockPanel` passes `onOpenPlan`, palette context)
- Modify: `web/src/shell/commands.ts` (`openPlanFile` in `CommandContext`, command `plan.open`)
- Modify: `docs/guide/analysis.md`, `docs/guide/de/analysis.md` (section "Execution plans")
- Test: `web/src/shell/commands.test.ts` if it lists command ids (check `grep -n "connection.bucket" web/src/shell/*.test.ts`), `web/src/plan/planFiles.test.ts`

**Interfaces:**
- Consumes: `PlanDocumentView`, `openPlanFile`, `AnalyzeResultDto`.
- Produces: `planFiles.put(result: AnalyzeResultDto): string` (id), `planFiles.get(id: string): AnalyzeResultDto | undefined`.

- [ ] **Step 1: The in-memory store and its test** — a plan can be megabytes; dockview persists panel params in local storage, so the tab carries only an id.

`web/src/plan/planFiles.ts`:

```ts
import type { AnalyzeResultDto } from "../api";

/// Opened plan files, by tab. In memory only: a plan can be megabytes, and the dock's saved layout
/// lives in local storage, which would take the whole plan with every layout change.
const files = new Map<string, AnalyzeResultDto>();
let next = 0;

export const planFiles = {
  put(result: AnalyzeResultDto): string {
    const id = `planfile-${Date.now().toString(36)}-${next++}`;
    files.set(id, result);
    return id;
  },
  get: (id: string) => files.get(id),
  drop: (id: string) => { files.delete(id); },
};
```

`web/src/plan/planFiles.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { planFiles } from "./planFiles";

describe("planFiles", () => {
  it("hands out distinct ids and gives the plan back", () => {
    const result = { plan: null, summary: null, planError: null, findings: [] };
    const a = planFiles.put(result);
    const b = planFiles.put(result);
    expect(a).not.toBe(b);
    expect(planFiles.get(a)).toBe(result);
    planFiles.drop(a);
    expect(planFiles.get(a)).toBeUndefined();
  });
});
```

- [ ] **Step 2: The dock panel** — in `DockShell.tsx`:

```tsx
function PlanFileDockPanel(props: IDockviewPanelProps<{ fileId: string; name: string }>) {
  const shell = useShell();
  const result = planFiles.get(props.params.fileId);
  // A restored layout remembers the tab, not the megabytes behind it.
  if (!result?.document) {
    return <Text size="xs" c="dimmed" p="xs">This plan was open in an earlier session. Open the file again to see it.</Text>;
  }

  return (
    <Stack gap={0} h="100%">
      <PlanDocumentView document={result.document} name={props.params.name}
        onRunStatement={statement => shell.runStatement(shell.tabs[0]?.connectionId ?? "", statement)} />
    </Stack>
  );
}
```

Register it in `components`: `planFile: PlanFileDockPanel`. Add, next to the other `open…` callbacks (pattern of the redis panel at line ~745):

```tsx
  const openPlanTab = useCallback((name: string, result: AnalyzeResultDto) => {
    const fileId = planFiles.put(result);
    api.current?.addPanel({
      id: fileId, component: "planFile", title: `Plan · ${name}`, params: { fileId, name },
      position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
    });
    flashPanel(api.current?.getPanel(fileId)?.group.element);
  }, []);
```

Expose it on the shell context (the object built near line 1491 that `useShell()` reads) as `openPlanTab`, add it to the context type (line ~90-110), and in `PlanDockPanel` pass `onOpenPlan={shell.openPlanTab}`. Findings of an opened file show under the document in the tab: below `PlanDocumentView` render `{result.findings.length > 0 && <Text size="xs" c="dimmed" px={6}>{result.findings.length} findings — see the Findings tab of the plan panel for live plans.</Text>}` is not enough — render the finding titles and details as a compact list:

```tsx
      {result.findings.length > 0 && (
        <ScrollArea.Autosize mah={160} px={6} style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>
          {result.findings.map((f, i) => (
            <Text key={i} size="xs"><b>{f.title}</b> — <span style={{ opacity: .7 }}>{f.detail}</span></Text>
          ))}
        </ScrollArea.Autosize>
      )}
```

Imports: `planFiles` from `../plan/planFiles`, `PlanDocumentView, openPlanFile` from `../plan/PlanDocumentView`, `type AnalyzeResultDto` from `../api`, `Stack, ScrollArea` from Mantine if not yet imported.

- [ ] **Step 3: Palette command** — `commands.ts`: in `CommandContext` add

```ts
  /// Asks for a .sqlplan or .xml and opens it as a plan tab.
  openPlanFile: () => void;
```

and after `explorer.goto`:

```ts
    { id: "plan.open", label: "Open execution plan — .sqlplan, .xml", group: "Query", run: context.openPlanFile },
```

In `DockShell.tsx` `buildCommands({ … })` add:

```tsx
    openPlanFile: () => {
      const input = document.createElement("input");
      input.type = "file";
      input.accept = ".sqlplan,.xml";
      input.onchange = () => {
        const file = input.files?.[0];
        if (!file) return;
        openPlanFile(file)
          .then(result => openPlanTab(file.name.replace(/\.(sqlplan|xml)$/i, ""), result))
          .catch(e => notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) }));
      };
      input.click();
    },
```

and `openPlanTab` to that `useMemo`'s dependency list. Fix any test that builds a full `CommandContext` object (`grep -rn "addBucket:" web/src --include=*.test.ts*`) by adding `openPlanFile: vi.fn()`.

- [ ] **Step 4: Docs** — in `docs/guide/analysis.md` add (and the German twin in `docs/guide/de/analysis.md`):

```markdown
## Execution plans

The **Plan** panel draws the plan the way SSMS does: right to left, from the tables to the result,
every operator with its share of the statement's cost, its rows (actual of estimated when the plan
ran) and its time, and arrows as thick as the rows they carry. Click an operator for every property
the server reported — predicates, output columns, the runtime counters of each thread. The search
box finds an operator or a table; Enter jumps to the next one.

On SQL Server the whole plan is there, including memory grant, waits, warnings and the indexes the
server asks for. **Save** writes it as `.sqlplan` or `.xml`, which SSMS and Rider open. **Open plan**
(the folder button, *Open execution plan* in the command palette, or a file dropped on the panel)
reads such a file back — from SSMS, from a colleague, from last week — into its own tab, without a
connection.
```

German:

```markdown
## Ausführungspläne

Das **Plan**-Panel zeichnet den Plan wie SSMS: von rechts nach links, von den Tabellen zum Ergebnis,
jeder Operator mit seinem Anteil an den Kosten, seinen Zeilen (tatsächlich von geschätzt, wenn der
Plan gelaufen ist) und seiner Zeit, und Pfeile so dick wie die Zeilen, die sie tragen. Ein Klick
auf einen Operator zeigt alles, was der Server dazu gemeldet hat – Prädikate, Ausgabespalten, die
Laufzeitzähler jedes Threads. Die Suche findet einen Operator oder eine Tabelle; Enter springt zum
nächsten.

Auf SQL Server ist der ganze Plan da, mit Memory Grant, Waits, Warnungen und den Indexen, die der
Server vermisst. **Save** schreibt ihn als `.sqlplan` oder `.xml`, die SSMS und Rider öffnen.
**Open plan** (der Ordner-Knopf, *Open execution plan* in der Befehlspalette oder eine aufs Panel
gezogene Datei) liest so eine Datei wieder ein – aus SSMS, von einem Kollegen, von letzter Woche –
in einen eigenen Tab, ohne Connection.
```

- [ ] **Step 5: Verify everything**

Run: `cd web && npx tsc -b && npx vitest run && npx oxlint src` — all pass.
Run: `dotnet test tests/WebDataStudio.Server.Tests` — all pass.
Run: `node scripts/check-links.mjs` — links resolve.
Manual: start API (`dotnet run --project src/WebDataStudio.Server --urls http://localhost:5000`, `DB_PATH` in the scratchpad) and `npm run dev` in `web`; with Playwright open the palette command, pick `C:\Users\User\Documents\plan.sqlplan`, screenshot the tab, select a node, check the properties, save as .sqlplan and compare the download byte-for-byte with the original.

- [ ] **Step 6: Commit**

```bash
git add web/src docs/guide
git commit -m "feat(plan): gespeicherte Pläne als eigener Tab öffnen, per Befehl, Ordner-Knopf oder Drag & Drop"
```
