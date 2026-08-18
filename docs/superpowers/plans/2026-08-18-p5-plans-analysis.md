# P5 — Execution Plans, Index Advisor and Deep Analyze Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show why a query is slow and say concretely what to do about it — a readable plan tree, a cost heat map, `CREATE INDEX` suggestions with reasons, and a health report per schema.

**Architecture:** Each driver already returns a normalised `PlanNode` tree (P1). This phase adds the analysis layer on top: a shared rule engine that walks a plan and a schema and emits `AnalyzeFinding` values, plus per-engine catalogue queries for the statistics those rules need. The UI renders the tree, the heat map and the findings, each finding carrying a runnable statement.

**Tech Stack:** the P1 driver layer, `@xyflow/react` for the plan graph, Mantine.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`

## Global Constraints

- Everything from P0–P4 global constraints still holds.
- A suggestion must be actionable: every finding carries either a runnable statement or a precise reason why no automatic fix exists.
- Actual plans execute the query. The UI must say so before running one, and must refuse for a statement the dialect classifies as a write unless the engine can wrap it in a rolled-back transaction.
- No analysis rule may run an unbounded scan against user data. Rules read catalogue and statistics views only.
- Feature IDs delivered by this phase: F9.1–F9.8.

---

### Task 1: Plan rule engine

**Files:**
- Create: `src/WebDataStudio.Server/Analysis/PlanRules.cs`
- Create: `src/WebDataStudio.Server/Analysis/PlanSummary.cs`
- Create: `tests/WebDataStudio.Server.Tests/Analysis/PlanRulesTests.cs`

**Interfaces:**
- Consumes: `PlanNode`, `AnalyzeFinding` from P1.
- Produces:
  - `PlanRules.Evaluate(PlanNode root) -> IReadOnlyList<AnalyzeFinding>`
  - `PlanSummary.Summarize(PlanNode root) -> record PlanSummary(double TotalCost, double MaxNodeCost, int NodeCount, PlanNode? Hottest)` — the heat map divides each node's cost by `MaxNodeCost`.

- [ ] **Step 1: Write the failing tests**

Build `PlanNode` trees by hand (no database needed) and assert: a sequential scan with more than
10 000 estimated rows yields a "missing index" finding naming the relation; a nested loop whose inner
side is a scan yields a "nested loop over a scan" finding; an actual row count more than ten times
the estimate yields a "stale statistics" finding suggesting ANALYZE; a sort marked as spilling to
disk yields a "sort spilled" finding suggesting more work memory; a clean index-scan plan yields no
findings; `Summarize` finds the most expensive node in a nested tree and counts every node.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter PlanRules`

- [ ] **Step 3: Implement the rules**

Each rule is a small function `(PlanNode, PlanContext) -> AnalyzeFinding?`, collected in a list so a
new rule is one entry. Severity is `info`, `warning` or `critical`. The rules read only the
normalised fields, so they work for every engine without branching.

- [ ] **Step 4: Run the tests and commit**

```bash
dotnet test --filter PlanRules
git add -A && git commit -m "feat: engine-independent execution plan rules"
```

---

### Task 2: Index advisor

**Files:**
- Create: `src/WebDataStudio.Server/Analysis/IndexAdvisor.cs`
- Create: `src/WebDataStudio.Server/Analysis/PredicateExtractor.cs`
- Create: `tests/WebDataStudio.Server.Tests/Analysis/IndexAdvisorTests.cs`

**Interfaces:**
- Consumes: `PlanNode`, `ObjectDetail`, `SqlDialect`.
- Produces:
  - `PredicateExtractor.Extract(string sql) -> IReadOnlyList<PredicateRef>` where `record PredicateRef(string Table, string Column, PredicateKind Kind)` and `PredicateKind` is `Equality`, `Range`, `Join`, `OrderBy` or `GroupBy`.
  - `IndexAdvisor.Suggest(string sql, PlanNode plan, IReadOnlyDictionary<string, ObjectDetail> tables, SqlDialect dialect) -> IReadOnlyList<AnalyzeFinding>`.

- [ ] **Step 1: Write the failing tests**

For the extractor: `WHERE active = true` yields an equality predicate on `active`; `WHERE created_at > $1`
yields a range predicate; `JOIN orders o ON o.person_id = p.id` yields join predicates on both sides;
`ORDER BY name` and `GROUP BY status` yield their kinds; a predicate inside a string literal is not
extracted; an alias resolves to its table.

For the advisor: a scan plus an equality predicate on an unindexed column suggests
`CREATE INDEX ... ON table (column)`; equality columns are ordered before range columns in a composite
suggestion; a column that already has a leading index produces no suggestion; a join column with no
index on the child side suggests one; the generated statement uses the target dialect's quoting; every
finding's detail states the reason and the plan node it came from.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter IndexAdvisor`

- [ ] **Step 3: Implement both**

`PredicateExtractor` is a tokenising scanner over the statement, not a full parser: it strips strings
and comments, then reads the clauses it recognises. Document that limitation in a comment — a
suggestion is a hint, and a missed predicate costs nothing while a wrong parse would.

`IndexAdvisor` composes: only suggest a composite index when the equality columns come from the same
table and the table has no index already leading with them.

- [ ] **Step 4: Run the tests and commit**

```bash
dotnet test --filter IndexAdvisor
git add -A && git commit -m "feat: index advisor with predicate extraction"
```

---

### Task 3: Deep analyze per engine

**Files:**
- Create: `src/WebDataStudio.Server/Drivers/PostgreSql/PostgreSqlAnalyzer.cs`
- Create: `src/WebDataStudio.Server/Drivers/MySql/MySqlAnalyzer.cs`
- Create: `src/WebDataStudio.Server/Drivers/SqlServer/SqlServerAnalyzer.cs`
- Create: `src/WebDataStudio.Server/Drivers/Sqlite/SqliteAnalyzer.cs`
- Modify: each driver's `AnalyzeAsync`
- Create: `tests/WebDataStudio.Server.Tests/Analysis/DeepAnalyzeContractTests.cs`

**Interfaces:**
- Consumes: `IDbSession`, `AnalyzeScope`.
- Produces: each driver's `AnalyzeAsync` returning findings for: unused indexes, duplicate indexes, unindexed foreign keys, stale statistics, table bloat, and tables without a primary key.

- [ ] **Step 1: Write the contract test**

A single suite parameterised over the tier-1 fixtures from P1: after seeding a table with a duplicate
index and an unindexed foreign key, `AnalyzeAsync(Schema)` must return a finding of category
`duplicate-index` and one of `unindexed-foreign-key`; every finding must carry a non-empty title, a
detail, and either a statement or an explicit null with a reason; running analyze twice must produce
identical output (no timestamps or random ordering in the findings).

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter DeepAnalyze`

- [ ] **Step 3: Implement the PostgreSQL analyzer**

Queries: `pg_stat_user_indexes` where `idx_scan = 0` for unused indexes; a self-join on `pg_index`
comparing `indkey` for duplicates; `pg_constraint` left-joined against `pg_index` for unindexed
foreign keys; `pg_stat_user_tables.last_analyze` older than seven days with more than 10 000 changes
for stale statistics; `pg_stat_user_tables.n_dead_tup` over 20 percent of live tuples for bloat, with
`VACUUM (ANALYZE) <table>` as the statement.

- [ ] **Step 4: Implement the MySQL analyzer**

`sys.schema_unused_indexes` for unused indexes (with a note when the sys schema is unavailable),
`information_schema.statistics` grouped by column list for duplicates, `information_schema.key_column_usage`
against `statistics` for unindexed foreign keys, `information_schema.tables.data_free` for bloat with
`OPTIMIZE TABLE` as the statement.

- [ ] **Step 5: Implement the SQL Server analyzer**

`sys.dm_db_index_usage_stats` for unused indexes, `sys.dm_db_missing_index_details` for missing ones
(this engine hands the answer over directly), `sys.dm_db_index_physical_stats` for fragmentation with
`ALTER INDEX ... REBUILD` as the statement, and `sys.stats` with `STATS_DATE` for stale statistics.

- [ ] **Step 6: Implement the SQLite analyzer**

The narrow set SQLite can support honestly: tables without a primary key, indexes with identical
column lists, foreign keys with no index, and a note that `ANALYZE` has never run when
`sqlite_stat1` is absent. Everything else stays out rather than being guessed at.

- [ ] **Step 7: Run the contract test and commit**

```bash
dotnet test --filter DeepAnalyze
git add -A && git commit -m "feat: per-engine deep analyze"
```

---

### Task 4: Analysis endpoints

**Files:**
- Modify: `src/WebDataStudio.Server/Endpoints/QueryEndpoints.cs`
- Create: `src/WebDataStudio.Server/Endpoints/AnalysisEndpoints.cs`
- Create: `tests/WebDataStudio.Server.Tests/Analysis/AnalysisEndpointTests.cs`

**Interfaces:**
- Produces:
  - `POST /api/query/plan` extended to return `{ plan, summary, findings }`.
  - `POST /api/query/analyze` → `{ findings }` combining plan rules and the index advisor for one statement.
  - `GET /api/analyze/{conn}?scope=schema&target=` → the deep-analyze report.
  - `GET /api/stats/{conn}` → server metrics (F9.8) and `GET /api/stats/{conn}/slow-queries` (F9.7), both capability-gated.

- [ ] **Step 1: Write the failing tests**

Plan for a SELECT returns a tree with at least one node and a summary; plan on an engine without
plan support returns 400 with a clear message; analyze returns findings for a query against an
unindexed column; deep analyze against the seeded schema returns the duplicate-index finding;
slow-queries returns 400 when `caps.SlowQueryLog` is false; an actual plan for a `DELETE` is refused
unless the engine supports a rolled-back transaction.

- [ ] **Step 2: Implement the endpoints and the metric queries**

Server metrics per engine: PostgreSQL from `pg_stat_database`, `pg_stat_activity` and `pg_locks`;
MySQL from `SHOW GLOBAL STATUS` and `performance_schema`; SQL Server from `sys.dm_os_performance_counters`
and `sys.dm_exec_requests` including the blocking chain. Each returns the same normalised
`ServerStats` record so the UI has one shape.

- [ ] **Step 3: Run the tests and commit**

```bash
dotnet test --filter Analysis
git add -A && git commit -m "feat: plan, analyze and server statistics endpoints"
```

---

### Task 5: Plan and advisor UI

**Files:**
- Create: `web/src/plan/PlanPanel.tsx`
- Create: `web/src/plan/PlanTree.tsx`
- Create: `web/src/plan/PlanGraph.tsx`
- Create: `web/src/plan/heat.ts`
- Create: `web/src/plan/heat.test.ts`
- Create: `web/src/analysis/AdvisorPanel.tsx`
- Create: `web/src/analysis/HealthReportPage.tsx`
- Create: `web/src/stats/ServerStatsPanel.tsx`

**Interfaces:**
- Consumes: the endpoints from Task 4.
- Produces:
  - `heatColor(cost: number, maxCost: number): string` — a CSS colour from the Mantine palette, tested for monotonicity and for handling `maxCost === 0`.
  - `<PlanPanel connectionId sql />` with Tree, Graph and Findings tabs, and an Estimated/Actual toggle.

- [ ] **Step 1: Write the failing test**

`heat.test.ts`: zero cost yields the coolest colour; max cost yields the hottest; a mid value yields
neither; `maxCost === 0` does not divide by zero and returns the coolest colour.

- [ ] **Step 2: Build the tree view**

A collapsible tree, one row per node: operation, relation, estimated rows, actual rows when present,
cost, and time. The row background is `heatColor(...)`. Warning nodes get an icon whose tooltip lists
the warnings from `PlanNode.Warnings`.

- [ ] **Step 3: Build the graph view**

`@xyflow/react` with `dagre` layout (the same pairing AspireUI uses for its canvas), nodes coloured by
heat, edges labelled with row counts. Clicking a node scrolls the tree view to it.

- [ ] **Step 4: Build the advisor panel**

One card per finding: severity chip, title, detail, and — when the finding carries a statement — a
Copy button and a "Run in new tab" button. Never an "apply automatically" button: an index change is
the user's call.

- [ ] **Step 5: Build the health report page**

Deep analyze for the selected connection or schema, findings grouped by category with counts, a
re-run button, and an export of the report as Markdown through the P3 exporter.

- [ ] **Step 6: Build the server statistics panel**

Connections, cache hit ratio, locks and blocking chains, refreshed on an interval the user controls
(off by default — a stats poll against a production server should be opt-in).

- [ ] **Step 7: Verify by hand**

Against PostgreSQL: run `SELECT * FROM people WHERE active = true` with no index on `active`, confirm
the plan shows a sequential scan, the advisor suggests the index, apply it manually and confirm the
new plan uses an index scan and the suggestion disappears.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "feat: plan tree, plan graph, index advisor and health report UI"
```

---

## Phase exit criteria

- Estimated and actual plans render as a tree and a graph with a working heat map for every engine
  whose capabilities claim plan support; engines without it hide the tab.
- The index advisor suggests a correct, runnable `CREATE INDEX` for an unindexed predicate, and stops
  suggesting once the index exists.
- Deep analyze reports duplicate indexes, unindexed foreign keys, stale statistics and bloat, each
  with a statement or an explicit reason there is none.
- Slow queries and server metrics appear for engines that support them and are hidden elsewhere.
- Feature IDs F9.1–F9.8 are demonstrably working.
