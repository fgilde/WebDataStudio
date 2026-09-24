# Execution plans you can read like SSMS

**Status:** approved in conversation, 2026-09-24 — SQL Server in full depth, every other engine gets
the same graphical view over the data it already returns.

## What is here now, and why it is not enough

The plan panel asks for an estimated or actual plan, shows it as an indented list of operators with
a heat colour, lists findings, trials a suggested index and compares against the previous run.

SQL Server hands back the whole Showplan XML — for a real query thousands of elements: seek
predicates, output lists, per-thread runtime counters, memory grants, waits, missing indexes. The
driver reads five attributes of each `RelOp` and throws the rest away. It also reads `ActualRows`
from the `RelOp`, where SQL Server never puts it: the actual counts live in
`RunTimeInformation/RunTimeCountersPerThread`, so an actual SQL Server plan has never shown an
actual row count.

What people compare it to is SSMS and Rider: a graph drawn right to left, an icon per operator,
cost percentages, arrows as thick as the rows they carry, and a property grid with everything the
server said. And the `.sqlplan` file, which is how a plan travels between people.

## Server

### The model

```csharp
public sealed record PlanProperty(string Name, string? Value, IReadOnlyList<PlanProperty> Children);

public sealed record PlanNode(
    string Operation, string? Detail,
    double? EstimatedCost, double? EstimatedRows, double? ActualRows, double? ActualMs,
    IReadOnlyList<PlanNode> Children, IReadOnlyList<string> Warnings,
    int? NodeId = null,                           // new: the engine's own id, SSMS shows it
    string? Object = null,                        // new: [schema].[table].[index] the node touches
    IReadOnlyList<PlanProperty>? Properties = null); // new: everything else, as a tree

public sealed record PlanStatement(
    string? Text, string? Type, double? Cost,
    IReadOnlyList<PlanProperty> Properties,       // memory grant, compile time, waits, …
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> MissingIndexes,         // ready-to-run CREATE INDEX statements
    PlanNode? Root);

public sealed record PlanDocument(
    string Engine,
    string? Raw,                                  // the engine's own text, for export
    string? RawFormat,                            // "sqlplan" today; null = not exportable
    IReadOnlyList<PlanStatement> Statements);
```

The new `PlanNode` fields are optional, so the fifteen existing constructions keep compiling and
every engine that does not fill them looks exactly as it does today.

### Where plans come from

`IDbDriver` gains

```csharp
Task<PlanDocument> ExplainDocumentAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
```

with a default interface implementation that wraps `ExplainAsync` into a one-statement document
with no raw text. SQL Server overrides it; its `ExplainAsync` becomes "the first statement's root of
the document", so the findings, the index trial and the MCP `explain_plan` tool read the richer
tree without changing.

### Parsing Showplan XML

`Drivers/SqlServer/ShowplanParser.cs`, static, `PlanDocument Parse(string xml)`:

- Statements: every `StmtSimple`, `StmtCond`, `StmtCursor` … in document order that has a
  `QueryPlan`, with `StatementText`, `StatementType`, `StatementSubTreeCost`.
- Statement properties: the `StmtSimple` attributes, `QueryPlan` attributes, `MemoryGrantInfo`,
  `OptimizerHardwareDependentProperties`, `QueryTimeStats`, `WaitStats` (one child per wait),
  `OptimizerStatsUsage`.
- Statement warnings: every child of `QueryPlan/Warnings`, spelled out (`SpillToTempDb`,
  `PlanAffectingConvert` with its expression, `MemoryGrantWarning`, `NoJoinPredicate`, …).
- Missing indexes: `MissingIndexGroup` turned into `CREATE INDEX` with equality, inequality and
  include columns and the impact in a comment.
- Nodes: every `RelOp`; children are the `RelOp`s whose nearest `RelOp` ancestor is this one.
  `Object` from the operator's own `Object` element. Actual rows summed over threads, actual ms the
  maximum `ActualElapsedms` over threads (threads run in parallel). Node warnings from the node's own
  `Warnings`, plus the two the driver already derives.
- Node properties: the `RelOp` attributes, then the operator element (`IndexScan`, `NestedLoops`,
  `Hash`, …): its attributes, `Object`, `SeekPredicates` / `Predicate` / `OuterReferences` /
  `HashKeysBuild` / `OrderBy` rendered as readable text (column references as
  `[table].[column]`, scalar operators by their `ScalarString`), `DefinedValues`, the `OutputList`,
  `MemoryFractions`, and `RunTimeInformation` with one child per thread.

Big plans are the point: the user's reference plan is 6 MB with 450 operators. The parser is one
pass over an `XDocument`, and the HTTP body limit (Kestrel's 30 MB) is left alone.

### Endpoints

- `POST /api/query/analyze` also returns `document`. `plan` and `summary` stay what they are.
- `POST /api/plans/open` with `{ text }`: recognises Showplan XML by its root element, parses it,
  and answers `{ document, plan, summary, findings }` with the plan-based findings (`PlanRules`, the
  missing indexes). No connection. Anything that is not Showplan XML is a 400 saying which formats
  it reads.

## Browser

### The view

`plan/PlanGraph.tsx` — `@xyflow/react` with `dagre` layout, both already dependencies:

- Right to left, as in SSMS: the root on the left, data sources on the right. Edges run from a
  child to its parent.
- A node card: an icon per operator family (scan, seek, lookup, join, sort, aggregate, spool,
  compute, filter, parallelism, insert/update/delete, other — Tabler icons, not Microsoft's),
  the operator, the object, the node's share of the statement cost in percent, actual of estimated
  rows, elapsed ms, a warning mark, and the heat colour the tree already uses.
- Edge thickness grows with the logarithm of the rows it carries (actual if known, else estimated);
  the label on hover says how many.
- `onlyRenderVisibleElements`, fit view on load, zoom and pan, a minimap for plans larger than the
  screen.
- A search box highlights nodes whose operator or object matches and steps through them.

`plan/PlanProperties.tsx` — the selected node's (or the statement's, when none is selected)
properties as an expandable tree with a filter box and copy-value.

`plan/PlanDocumentView.tsx` — the whole thing: statement picker when there is more than one, a
header line (cost, memory grant, compile time, DOP, statement warnings), missing indexes with copy
and "open in a new query tab", then graph and properties side by side.

The plan panel gets a **Graph** tab, first and default, rendering `PlanDocumentView`. Tree, Since
the last run and Findings stay as they are.

### Export and open

- **Export**: in the plan panel and the file tab, when `document.rawFormat === "sqlplan"`: *Save as
  .sqlplan* and *Save as .xml*, a Blob download of `raw`. No server round trip.
- **Open**: "Open plan…" in the plan panel toolbar, "Open execution plan" in the command palette,
  and a `.sqlplan`/`.xml` file dropped on the plan panel. The file goes to `/api/plans/open`; the
  answer opens a dock tab `plan-file` titled after the file, showing `PlanDocumentView` and the
  findings, without estimated/actual and without the index trial (there is no connection to try it
  on).

## Tests

- C#: `ShowplanParserTests` over a small hand-written Showplan fixture (two statements, a nested
  loop over a seek and a scan, runtime counters on two threads, a missing index, a warning): tree
  shape, actual rows summed, ms maximum, object names, predicates as text, missing-index DDL,
  statement properties. `/api/plans/open` accepts the fixture and refuses non-plan text.
- The user's 6 MB reference plan is **not** committed — it carries a production schema and query.
  It is used locally to check parse time and that nothing throws.
- TS: layout (right-to-left ranks, edge widths monotone in rows), operator-to-icon mapping, the
  properties filter, and a PlanPanel test that export downloads the raw text and open lands in the
  callback.

## Left out

- Microsoft's operator icons (theirs to license).
- Export for other engines, and opening PostgreSQL/MySQL plan text.
- Side-by-side comparison of two plan files; the comparison with the previous run stays.
