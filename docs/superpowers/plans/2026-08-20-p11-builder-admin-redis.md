# Builder, Administration, Redis and the power features — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the two half-built surfaces (visual query builder, administration) into tools people
reach for, give Redis the same depth the SQL engines already have, and add the five features that
set the studio apart: federation over DuckDB, undo for data changes, masking of sensitive columns,
a watch mode, and generated test data.

**Architecture:** Every phase follows the shape the codebase already has — a capability flag on the
driver decides whether a surface appears, the server owns the SQL/commands and streams results, the
SPA renders and never composes engine-specific SQL itself, and anything that writes goes through the
existing preview-then-apply handshake (SHA-256 of the script, `IMemoryCache`, explicit confirm).
New panels are dockview panels registered in `DockShell`, so they inherit the layout, presets and
the activation flash for free.

**Tech Stack:** .NET 10 minimal APIs, xunit v3 with Microsoft.Testing.Platform, Testcontainers for
engine-backed tests; React 19, Mantine 9, dockview-react, Monaco, `@xyflow/react` + `@dagrejs/dagre`
(already present, used by the ER diagram), `@tanstack/react-virtual`, Vitest, Playwright smokes.
DuckDB.NET and StackExchange.Redis are already dependencies.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md` — every feature id this plan
introduces is added there and to `docs/features.md` in the same commit as the feature, because
`FeatureCoverageTests` fails the build when the two drift apart.

## Global Constraints

- **No new runtime dependency** unless a phase names it explicitly and nothing already installed
  can do the job. `@xyflow/react`, `dagre`, DuckDB.NET, StackExchange.Redis, MiniExcel and
  Parquet.Net are already in.
- **Capability-gated, never hard-coded per engine.** A surface that only some engines support reads
  a flag from `DriverCapabilities`; the flag is set in the driver, and
  `CapabilityHonestyTests` requires that a driver claiming a capability actually answers.
- **Every write is previewed.** New mutating endpoints return a script plus a hash; a second call
  applies that hash. No endpoint mutates on the first call.
- **Object references travel in the query string** (`?ref=…`), never in a path segment: a reverse
  proxy decodes `%2F` into a slash and the route stops matching. `ProxySafeRoutesTests` enforces it.
- **The workspace database is local storage only** and its absence must stay survivable — reads
  degrade, writes answer 503.
- **Read-only is enforced in the driver**, not by hiding buttons, and `WDS_READONLY` overrides
  everything per instance.
- **Tests:** every server behaviour gets a test in `tests/WebDataStudio.Server.Tests`; pure
  front-end logic gets a Vitest test next to the file; anything that only breaks in a browser gets
  a Playwright smoke in `web/scripts/`.
- **Docs:** `docs/features.md`, the spec table, and the matching guide page under `docs/guide/`
  are part of the task that introduces the feature, not a follow-up.

---

## Phase 1 — The query builder as a canvas (F17)

Today `web/src/designer/QueryDesigner.tsx` is a 251-line form: pick tables from a flat select, type
join conditions, get a SELECT. The tables it needs are already described by
`describeObject` — including foreign keys — so the joins can be proposed rather than typed, and the
ER diagram already proves the canvas stack works.

### Task 1.1: Propose joins from foreign keys

**Files:**
- Modify: `web/src/designer/buildSelect.ts` (add `suggestJoin`)
- Modify: `web/src/designer/QueryDesigner.tsx` (call it when a table is added)
- Test: `web/src/designer/suggestJoin.test.ts`

**Interfaces:**
- Produces: `suggestJoin(left: LoadedTable, right: LoadedTable): { on: string; kind: JoinKind } | null`
  where `LoadedTable = { alias: string; ref: string; columns: string[]; foreignKeys: ForeignKeyDto[] }`.

- [ ] **Step 1: Write the failing test** — a table whose foreign key points at an already-loaded
      table yields `a.id = b.person_id`; two unrelated tables yield `null`; the direction is
      respected, so adding the parent after the child still finds it.

```ts
it("joins a child to the parent it references", () => {
  const people = { alias: "a", ref: "Table:main/people", columns: ["id"], foreignKeys: [] };
  const orders = { alias: "b", ref: "Table:main/orders", columns: ["id", "person_id"],
    foreignKeys: [{ name: "fk", columns: ["person_id"], referencedTable: "people",
                    referencedSchema: "main", referencedColumns: ["id"] }] };

  expect(suggestJoin(people, orders)).toEqual({ on: "a.id = b.person_id", kind: "inner" });
});
```

- [ ] **Step 2: Run it** — `npx vitest run src/designer/suggestJoin.test.ts`, expect failure
      "suggestJoin is not exported".
- [ ] **Step 3: Implement** `suggestJoin` in `buildSelect.ts`: match `referencedTable` (and schema
      when both have one) against the other table's name, pair the column lists positionally, and
      return `null` when nothing matches. Load `foreignKeys` in `QueryDesigner.addTable` from the
      `describeObject` response that is already fetched there.
- [ ] **Step 4: Run it** — expect pass, then `npx tsc --noEmit`.
- [ ] **Step 5: Commit** — `feat(builder): propose a join from the foreign key when a table joins the canvas`

### Task 1.2: The canvas

**Files:**
- Create: `web/src/designer/QueryCanvas.tsx` — tables as xyflow nodes, joins as edges
- Modify: `web/src/designer/QueryDesigner.tsx` — canvas on top, the existing lists below it
- Test: `web/src/designer/canvasModel.test.ts` (pure mapping model ↔ nodes/edges)

**Interfaces:**
- Consumes: `QueryModel`, `suggestJoin` from Task 1.1.
- Produces: `toGraph(model: QueryModel, loaded: LoadedTable[]): { nodes: Node[]; edges: Edge[] }`
  and `applyEdge(model: QueryModel, connection: { source: string; target: string }): QueryModel`.

- [ ] **Step 1: Write the failing test** for `toGraph`: one node per table with its alias as id and
      its columns in `data.columns`; one edge per join with the join kind as its label. And for
      `applyEdge`: dragging from `a` to `b` adds a join whose `on` comes from `suggestJoin`, or an
      empty `on` the user fills in.
- [ ] **Step 2: Run it** — expect failure.
- [ ] **Step 3: Implement** `canvasModel.ts` with both functions, then `QueryCanvas.tsx` rendering
      `<ReactFlow>` with a node type that lists columns and a checkbox per column bound to
      `model.columns`. Layout with `dagre` on first render, exactly as `web/src/diagram/layout.ts`
      does — reuse that module rather than a second copy.
- [ ] **Step 4: Run it** — Vitest green, `tsc` clean, and the panel renders in the app.
- [ ] **Step 5: Commit** — `feat(builder): build the query on a canvas instead of in a form`

### Task 1.3: Live SQL and a sample of the result

**Files:**
- Modify: `web/src/designer/QueryDesigner.tsx`
- Test: `web/scripts/smoke-builder.mjs` (new smoke), `package.json` script `smoke:builder`

- [ ] **Step 1: Write the smoke** — open the builder, add `people`, add `orders`, assert the SQL box
      contains `LEFT JOIN` or `JOIN orders`, assert the preview grid shows a value from the seeded
      data, assert no console errors.
- [ ] **Step 2: Run it** — fails, because there is no preview grid.
- [ ] **Step 3: Implement** — debounce 400 ms, run the generated SQL through the existing
      `runQuery` API with `LIMIT 50` applied by the dialect's `Paginate`, render with `ResultGrid`.
      A failing preview shows the server error inline and never blocks editing.
- [ ] **Step 4: Run it** — smoke green.
- [ ] **Step 5: Commit** — `feat(builder): show the SQL and the first rows while the query is built`

### Task 1.4: Aggregates, HAVING, DISTINCT and the round trip

**Files:**
- Modify: `web/src/designer/buildSelect.ts`, `QueryDesigner.tsx`
- Test: `web/src/designer/buildSelect.test.ts` (extend)

**Interfaces:**
- Produces: `QueryModel` gains `columns: { table: string; name: string; aggregate?: Aggregate; alias?: string }[]`,
  `having: Filter[]`, `distinct: boolean`, where `Aggregate = "count" | "sum" | "avg" | "min" | "max"`.

- [ ] **Step 1: Write the failing tests** — an aggregate column puts every non-aggregate column
      into `GROUP BY`; `having` renders after `GROUP BY`; `distinct` renders as `SELECT DISTINCT`;
      `buildSelect` ends with a `/* wds:model {…} */` comment carrying the JSON model, and
      `parseModel(sql)` returns that model again (and `null` for hand-written SQL).
- [ ] **Step 2: Run them** — expect failure.
- [ ] **Step 3: Implement** the model fields, the rendering, and `parseModel`. The builder offers
      "open in builder" for any query tab whose SQL carries the comment.
- [ ] **Step 4: Run them** — green, plus `smoke:builder`.
- [ ] **Step 5: Commit** — `feat(builder): aggregates, HAVING, DISTINCT, and a query that can be reopened`

### Task 1.5: Documentation

- [ ] **Step 1:** Add `F17.1`–`F17.5` to the spec table and to `docs/features.md` (canvas,
      foreign-key joins, live preview, aggregates, round trip).
- [ ] **Step 2:** Write `docs/guide/query-builder.md` and link it in `docs/guide/_sidebar.md`.
- [ ] **Step 3:** Run `node scripts/check-links.mjs docs` and the feature coverage test.
- [ ] **Step 4: Commit** — `docs(builder): the canvas, the joins it proposes and the round trip`

---

## Phase 2 — Administration that leads to action (F18)

`web/src/admin/AdminPanel.tsx` has eight tabs, each showing one query. What is missing is the view
that answers "what is going on right now", the progress of work already running, and a path from a
recommendation to a change.

### Task 2.1: Server-side progress and blocking

**Files:**
- Create: `src/WebDataStudio.Server/Services/ServerActivity.cs` — per-engine SQL for running
  operations and lock waits
- Modify: `src/WebDataStudio.Server/Endpoints/AdminEndpoints.cs` — `GET /api/admin/activity/{conn}`
- Modify: `src/WebDataStudio.Server/Drivers/Abstractions/DriverCapabilities.cs` — add
  `ActivityProgress`, `BlockingChains`
- Test: `tests/WebDataStudio.Server.Tests/Admin/ActivityTests.cs`

**Interfaces:**
- Produces: `record ActivityDto(IReadOnlyList<RunningOperation> Operations, IReadOnlyList<LockWait> Waits)`,
  `record RunningOperation(string Id, string Kind, string Target, double? PercentComplete, long ElapsedMs, string? Statement)`,
  `record LockWait(string Blocker, string Blocked, string Resource, long WaitMs, string? Statement)`.

- [ ] **Step 1: Write the failing test** — against the PostgreSQL Testcontainer, two connections
      where one holds a row lock: the endpoint reports a wait whose blocker is the first session's
      pid, and the shape is stable when nothing is blocked (empty arrays, 200).
- [ ] **Step 2: Run it** — fails, endpoint missing.
- [ ] **Step 3: Implement** the per-engine queries: PostgreSQL `pg_stat_progress_*` plus
      `pg_locks`/`pg_blocking_pids`, SQL Server `sys.dm_exec_requests` (`percent_complete`,
      `blocking_session_id`), MySQL `performance_schema.data_locks` +
      `information_schema.processlist`, Oracle `v$session_longops` + `v$lock`. Anything else
      reports the capability as false and the tab does not appear.
- [ ] **Step 4: Run it** — green; `CapabilityHonestyTests` still green.
- [ ] **Step 5: Commit** — `feat(admin): report running operations and who is blocking whom`

### Task 2.2: The overview tab

**Files:**
- Create: `web/src/admin/Overview.tsx` — tiles with a sparkline each
- Modify: `web/src/admin/AdminPanel.tsx` — new first tab
- Test: `web/src/admin/history.test.ts` (the ring buffer that feeds the sparklines)

**Interfaces:**
- Produces: `useMetricHistory(sample: () => Promise<Sample>, intervalMs: number, keep: number)`
  returning `{ samples: Sample[]; latest: Sample | null; error: string | null }`.

- [ ] **Step 1: Write the failing test** for the ring buffer: it keeps at most `keep` samples,
      drops the oldest, survives a failing sample without losing history, and stops polling when
      unmounted.
- [ ] **Step 2: Run it** — expect failure.
- [ ] **Step 3: Implement** the hook and the tiles: connections, cache hit ratio, locks waiting,
      longest running statement, database size, plus the sparkline per tile. Follow the `dataviz`
      guidance for the chart colours; reuse `ResultChart`'s palette rather than inventing one.
- [ ] **Step 4: Run it** — green, `tsc` clean.
- [ ] **Step 5: Commit** — `feat(admin): an overview tab that answers what is happening right now`

### Task 2.3: Blocking chains as a tree, with a kill button

**Files:**
- Create: `web/src/admin/BlockingTree.tsx`
- Modify: `web/src/admin/AdminPanel.tsx` (sessions tab shows the tree above the list)
- Test: `web/src/admin/blockingTree.test.ts`

**Interfaces:**
- Produces: `toChains(waits: LockWait[]): ChainNode[]` with
  `ChainNode = { session: string; statement?: string; waitMs: number; blocked: ChainNode[] }`.

- [ ] **Step 1: Write the failing test** — a chain a→b→c becomes one root with a nested child; two
      independent pairs become two roots; a cycle (which SQL Server can report) does not recurse
      forever.
- [ ] **Step 2: Run it** — expect failure.
- [ ] **Step 3: Implement** `toChains` and the tree, with the existing kill action on each node.
- [ ] **Step 4: Run it** — green.
- [ ] **Step 5: Commit** — `feat(admin): show the blocking chain as a tree and kill at the root`

### Task 2.4: Recommendations that can be applied

**Files:**
- Modify: `src/WebDataStudio.Server/Endpoints/AnalysisEndpoints.cs` — every finding carries a
  `script`
- Modify: `web/src/plan/PlanPanel.tsx` and the health report — a "Show script" action per finding
  that opens the existing migration preview
- Test: `tests/WebDataStudio.Server.Tests/Analysis/RecommendationScriptTests.cs`

- [ ] **Step 1: Write the failing test** — the deep-analyze response for a table with an unindexed
      foreign key contains a finding whose `script` is a `CREATE INDEX` statement that parses for
      the engine's dialect, and a duplicate index yields a `DROP INDEX`.
- [ ] **Step 2: Run it** — fails, findings have no script today.
- [ ] **Step 3: Implement** — generate the script next to each finding using the driver's dialect
      helpers (`objectScripts.ts` has the shapes for the front end; the server side belongs in
      `Services/IndexAdvisor.cs`). Apply goes through `POST /api/ddl/{conn}/preview` and `apply`,
      which already exist — no new mutation path.
- [ ] **Step 4: Run it** — green.
- [ ] **Step 5: Commit** — `feat(admin): every recommendation carries the script that fixes it`

### Task 2.5: Replication, sizes and a configuration diff

**Files:**
- Modify: `src/WebDataStudio.Server/Services/ServerActivity.cs` (replication), new
  `Services/ConfigSnapshot.cs` (settings), `Endpoints/AdminEndpoints.cs`
  (`GET /api/admin/replication/{conn}`, `GET /api/admin/config/{conn}`)
- Create: `web/src/admin/Replication.tsx`, `web/src/admin/SizeTreemap.tsx`,
  `web/src/admin/ConfigDiff.tsx`
- Test: `tests/WebDataStudio.Server.Tests/Admin/ReplicationTests.cs`,
  `web/src/admin/treemap.test.ts`

**Interfaces:**
- Produces: `record ReplicaDto(string Name, string Role, string State, long? LagBytes, long? LagSeconds)`,
  `record SettingDto(string Name, string Value, string? Default, string? Unit, bool RequiresRestart)`,
  and `squarify(items: {label: string; bytes: number}[], width: number, height: number): Rect[]`.

- [ ] **Step 1: Write the failing tests** — replication against the PostgreSQL container reports the
      primary with no replicas rather than failing; `squarify` fills the box, keeps the largest item
      first and never returns a negative rectangle.
- [ ] **Step 2: Run them** — expect failure.
- [ ] **Step 3: Implement** the queries (`pg_stat_replication`, `SHOW REPLICA STATUS`,
      `sys.dm_hadr_database_replica_states`), the settings snapshot (`pg_settings`,
      `SHOW VARIABLES`, `sys.configurations`), the treemap from the table-size data the health
      report already fetches, and the config diff which reuses `web/src/compare/diffResults.ts`
      to compare two connections' settings.
- [ ] **Step 4: Run them** — green.
- [ ] **Step 5: Commit** — `feat(admin): replication state, a size treemap and a settings diff`

### Task 2.6: Documentation

- [ ] Add `F18.1`–`F18.6` to the spec and `docs/features.md`; extend
      `docs/guide/administration.md`; run the link check and the coverage test.
- [ ] **Commit** — `docs(admin): the overview, blocking chains, replication and the settings diff`

---

## Phase 3 — Redis with the depth the SQL engines have (F19)

`RedisDriver` browses keys by `:` prefix, describes type, TTL and length, and runs commands line by
line. Everything below is what a Redis user expects and cannot do today. RedisInsight is the
reference for scope, not for code.

### Task 3.1: A key browser that scans instead of listing

**Files:**
- Create: `src/WebDataStudio.Server/Drivers/Redis/RedisKeyspace.cs`
- Modify: `src/WebDataStudio.Server/Drivers/Redis/RedisDriver.cs` (delegate SCAN to it)
- Create: `src/WebDataStudio.Server/Endpoints/RedisEndpoints.cs` —
  `GET /api/redis/{conn}/keys?db=0&match=user:*&type=hash&cursor=0&count=200`
- Test: `tests/WebDataStudio.Server.Tests/Redis/KeyspaceTests.cs`

**Interfaces:**
- Produces: `record KeyPageDto(IReadOnlyList<KeyDto> Keys, long NextCursor, bool Complete)`,
  `record KeyDto(string Key, string Type, long? TtlSeconds, long? SizeBytes, long? Length)`.

- [ ] **Step 1: Write the failing test** — against the Redis Testcontainer with 5 000 keys: the
      first page returns at most `count` keys and a non-zero cursor, following the cursor to the end
      returns every key exactly once, `match` and `type` filter server-side, and `SizeBytes` comes
      from `MEMORY USAGE`.
- [ ] **Step 2: Run it** — fails, no endpoint.
- [ ] **Step 3: Implement** cursor-based scanning (drop the 5 000-key ceiling in the tree: the tree
      shows prefixes and asks for a page per prefix), TTL and size per key in one pipeline.
- [ ] **Step 4: Run it** — green.
- [ ] **Step 5: Commit** — `feat(redis): scan the keyspace with a cursor, a pattern and a type filter`

### Task 3.2: Type-aware value editors

**Files:**
- Modify: `src/WebDataStudio.Server/Endpoints/RedisEndpoints.cs` —
  `GET /api/redis/{conn}/value?db=&key=`, `POST …/value` (preview + apply like every other write)
- Create: `web/src/redis/KeyBrowser.tsx`, `web/src/redis/ValueEditor.tsx`,
  `web/src/redis/editors/{StringEditor,HashEditor,ListEditor,SetEditor,ZSetEditor,StreamEditor,JsonEditor}.tsx`
- Modify: `web/src/dock/DockShell.tsx` — a `redis` panel, opened from the explorer for a Redis
  connection
- Test: `tests/WebDataStudio.Server.Tests/Redis/ValueTests.cs`, `web/src/redis/format.test.ts`

**Interfaces:**
- Produces: `record ValueDto(string Key, string Type, long? TtlSeconds, JsonElement Value, long Length, string? Encoding)`
  and `record ValueEditRequest(string Key, string Type, string Operation, JsonElement Payload)`
  with operations `set`, `hset`, `hdel`, `rpush`, `lset`, `lrem`, `sadd`, `srem`, `zadd`, `zrem`,
  `xadd`, `json.set`, `expire`, `persist`, `rename`, `del`.
- Produces (front end): `detectFormat(value: string): "json" | "hex" | "text"`.

- [ ] **Step 1: Write the failing tests** — server: reading a hash returns its fields as an object
      and a list returns an array with indices; writing goes through preview (a hash whose apply
      hash is wrong is rejected); `expire` sets a TTL that `TTL` then reports. Front end:
      `detectFormat` recognises JSON, hex-looking blobs and plain text.
- [ ] **Step 2: Run them** — expect failure.
- [ ] **Step 3: Implement** the endpoints, then one editor per type: string with a format switch
      (raw / JSON tree / hex), hash and zset as virtualised tables with inline edit, list with
      index and push/pop, set with add/remove, stream with entries and a "add entry" form, JSON via
      `JSON.GET`/`JSON.SET` when the module answers.
- [ ] **Step 4: Run them** — green, `tsc` clean.
- [ ] **Step 5: Commit** — `feat(redis): edit every value type from a browser built for it`

### Task 3.3: Bulk actions with a dry run

**Files:**
- Modify: `src/WebDataStudio.Server/Endpoints/RedisEndpoints.cs` —
  `POST /api/redis/{conn}/bulk/preview`, `POST …/bulk/apply`
- Create: `web/src/redis/BulkActions.tsx`
- Test: `tests/WebDataStudio.Server.Tests/Redis/BulkTests.cs`

**Interfaces:**
- Produces: `record BulkRequest(int Database, string Match, string? Type, string Action, long? TtlSeconds)`
  with actions `delete`, `expire`, `persist`; preview answers
  `record BulkPreviewDto(string Hash, long MatchedKeys, IReadOnlyList<string> Sample)`.

- [ ] **Step 1: Write the failing test** — a preview over `user:*` counts the matching keys and
      returns at most 20 samples without deleting anything; applying the hash deletes exactly those
      keys and leaves `order:*` untouched; a read-only connection is refused.
- [ ] **Step 2: Run it** — expect failure.
- [ ] **Step 3: Implement** — scan for matches, apply in batches through a pipeline, count what was
      done, and refuse when `Spec.ReadOnly`.
- [ ] **Step 4: Run it** — green.
- [ ] **Step 5: Commit** — `feat(redis): delete or expire by pattern, after showing what matches`

### Task 3.4: Keyspace analysis

**Files:**
- Create: `src/WebDataStudio.Server/Drivers/Redis/RedisAnalysis.cs`
- Modify: `Endpoints/RedisEndpoints.cs` — `GET /api/redis/{conn}/analysis?db=&sample=10000`
- Create: `web/src/redis/KeyspaceAnalysis.tsx`
- Test: `tests/WebDataStudio.Server.Tests/Redis/AnalysisTests.cs`

**Interfaces:**
- Produces: `record KeyspaceAnalysisDto(long SampledKeys, IReadOnlyList<PrefixStat> Prefixes, IReadOnlyList<TypeStat> Types, IReadOnlyList<KeyDto> Largest, IReadOnlyList<KeyDto> ExpiringSoon, long? TotalMemoryBytes)`.

- [ ] **Step 1: Write the failing test** — with 1 000 keys under two prefixes and one deliberately
      large value: the prefix table sums memory per prefix, the type table counts per type, and
      `Largest` is sorted descending with the big key first.
- [ ] **Step 2: Run it** — expect failure.
- [ ] **Step 3: Implement** — sample with `SCAN` (never `KEYS`), `MEMORY USAGE` per key in a
      pipeline, group by the prefix before the first `:`, and render prefixes as the treemap from
      Task 2.5 plus tables for the rest.
- [ ] **Step 4: Run it** — green.
- [ ] **Step 5: Commit** — `feat(redis): analyse the keyspace by prefix, type and memory`

### Task 3.5: Pub/Sub, streams with consumer groups, slowlog and clients

**Files:**
- Modify: `Endpoints/RedisEndpoints.cs` — `GET /api/redis/{conn}/subscribe?channels=` as
  server-sent events, `POST …/publish`, `GET …/stream?key=` (groups, pending),
  `GET …/slowlog`, `GET …/clients`
- Create: `web/src/redis/PubSub.tsx`, `web/src/redis/StreamPanel.tsx`,
  `web/src/redis/SlowLog.tsx`
- Test: `tests/WebDataStudio.Server.Tests/Redis/PubSubTests.cs`,
  `tests/WebDataStudio.Server.Tests/Redis/StreamTests.cs`

**Interfaces:**
- Produces: `record StreamInfoDto(long Length, string? FirstId, string? LastId, IReadOnlyList<ConsumerGroupDto> Groups)`,
  `record ConsumerGroupDto(string Name, long Consumers, long Pending, string LastDelivered)`,
  `record SlowEntryDto(long Id, DateTimeOffset At, long MicroSeconds, string Command, string? Client)`.

- [ ] **Step 1: Write the failing tests** — publishing on a channel while subscribed delivers the
      message through the SSE endpoint within a second; a stream with a consumer group reports the
      group, its pending count and the last delivered id; `slowlog` returns entries after a
      deliberately slow command with the threshold lowered.
- [ ] **Step 2: Run them** — expect failure.
- [ ] **Step 3: Implement** — one subscription per request, cancelled with the request; SSE keeps
      the browser side to `EventSource` with no new dependency. Streams read `XINFO STREAM/GROUPS`
      and `XPENDING`; the panel offers claim and ack on a pending entry.
- [ ] **Step 4: Run them** — green.
- [ ] **Step 5: Commit** — `feat(redis): live pub/sub, stream consumer groups, slow log and clients`

### Task 3.6: Command help in the console, and cluster awareness

**Files:**
- Create: `web/src/redis/commandHelp.ts` (generated from `COMMAND DOCS`, cached per connection)
- Modify: `web/src/editor/completion.ts` — Redis completion from that help
- Modify: `Endpoints/RedisEndpoints.cs` — `GET /api/redis/{conn}/commands`, `GET …/cluster`
- Test: `web/src/redis/commandHelp.test.ts`, `tests/WebDataStudio.Server.Tests/Redis/CommandDocsTests.cs`

- [ ] **Step 1: Write the failing tests** — the help index maps `HSET` to its arity and summary and
      suggests it for the prefix `hs`; the cluster endpoint reports a single node for a standalone
      server rather than failing.
- [ ] **Step 2: Run them** — expect failure.
- [ ] **Step 3: Implement** — `COMMAND DOCS` where available with a fallback to `COMMAND INFO`,
      completion and a hover with the summary in the console, and `CLUSTER INFO`/`CLUSTER NODES`
      for the slot map.
- [ ] **Step 4: Run them** — green.
- [ ] **Step 5: Commit** — `feat(redis): command help in the console and a cluster view`

### Task 3.7: Documentation and capabilities

- [ ] Set the new capability flags (`KeyBrowser`, `PubSub`, `Streams`, `KeyspaceAnalysis`) on the
      Redis driver only, extend `docs/guide/engines.md`, write `docs/guide/redis.md`, add
      `F19.1`–`F19.7` to the spec and `docs/features.md`, and mark `F14.2` as `done`.
- [ ] Add `web/scripts/smoke-redis.mjs` driving the browser: create a key, edit it, set a TTL,
      delete by pattern, see the analysis. Requires a Redis connection in the environment; the
      script skips with a clear message when there is none.
- [ ] **Commit** — `docs(redis): the key browser, the editors and everything around them`

---

## Phase 4 — Safety: masking, undo, and more than one user (F20)

### Task 4.1: Sensitive columns, masked by default in production

**Files:**
- Create: `src/WebDataStudio.Server/Services/SensitiveColumns.cs` (heuristic + per-connection
  overrides in the workspace store)
- Modify: `Endpoints/DataEndpoints.cs`, `QueryEndpoints.cs` (mask on the way out),
  `Endpoints/ExportEndpoints.cs` (mask or refuse)
- Create: `web/src/grid/MaskedCell.tsx`; modify `ResultGrid` to reveal per cell on request
- Test: `tests/WebDataStudio.Server.Tests/SensitiveColumnsTests.cs`

**Interfaces:**
- Produces: `bool IsSensitive(string column)` (matches `password`, `passwd`, `secret`, `token`,
  `iban`, `ssn`, `credit`, `cvv`, `email` case-insensitively, whole word or snake/camel segment),
  and `record MaskPolicy(bool MaskByDefault, IReadOnlySet<string> Extra, IReadOnlySet<string> Never)`.

- [x] **Step 1: Write the failing tests** — `user_password` and `userPassword` are sensitive,
      `password_changed_at` is not (it is a timestamp, not a secret), an explicit `Never` entry wins
      over the heuristic, and a masked column arrives as `"••••"` with a `masked: true` marker in
      the column metadata.
- [x] **Step 2: Run them** — expect failure.
- [x] **Step 3: Implement** the heuristic with `GeneratedRegex`, the per-connection policy stored in
      the workspace store, masking in the read paths, and an export that refuses unless the caller
      passes `includeSensitive=true` — which is only accepted when the connection is not marked
      production-coloured.

      *As built:* an export is **masked** rather than refused — `includeSensitive=true` returns the
      real values, and only a connection marked production (red) refuses that. Refusing the whole
      export because one column looks like a password blocks work the mask already makes safe.
      Reveal in the grid is a fresh request (`?reveal=true`), and the query stream is masked too,
      because it is the other way into the same data. The policy is editable per column from the
      data tab's column menu (`GET`/`PUT /api/data/{conn}/mask-policy`).
- [x] **Step 4: Run them** — green.
- [x] **Step 5: Commit** — `feat(safety): mask what looks like a secret, and make revealing it deliberate`

### Task 4.2: Undo for data changes

**Files:**
- Modify: `src/WebDataStudio.Server/Editing/ChangeScript.cs` — build the inverse script
- Modify: `Endpoints/DataEndpoints.cs` — store the inverse with the applied change, expose
  `POST /api/data/{conn}/undo?ref=` with the same preview/apply handshake
- Modify: `web/src/data/DataTab.tsx` — an undo button that shows the inverse script first
- Test: `tests/WebDataStudio.Server.Tests/Editing/UndoTests.cs`

**Interfaces:**
- Produces: `string BuildInverse(IReadOnlyList<Change> changes, IReadOnlyList<Row> before)` and
  `record UndoEntry(string Id, string ConnectionId, string ObjectRef, string Script, DateTimeOffset At)`.

- [x] **Step 1: Write the failing test** — updating two rows and undoing restores the old values
      exactly; an insert's inverse is a delete by key; a delete's inverse re-inserts every column;
      undoing twice is refused because the entry is consumed.
- [x] **Step 2: Run it** — expect failure.
- [x] **Step 3: Implement** — capture the affected rows inside the same transaction that applies
      the change, generate the inverse from them, keep the last N (default 20) entries per
      connection in the workspace store, and refuse when the store is unavailable rather than
      pretending the change is reversible.

      *As built:* the undo goes through the same preview-then-apply handshake as any other change
      (`POST /api/data/{conn}/undo/preview` caches the inverse under `changes:{hash}`, the existing
      `apply-changes` executes it and consumes the entry). An apply reports `undoable`, which is
      false when the inverse could not be built (a generated key) or not stored (no workspace) —
      the button then simply is not offered. An undo is not itself recorded, so there is no
      "undo the undo".
- [x] **Step 4: Run it** — green.
- [x] **Step 5: Commit** — `feat(safety): undo the last data change, script shown first`

### Task 4.3: Several users, each with their own connections

**Files:**
- Modify: `src/WebDataStudio.Server/Services/AuthOptions.cs` — a user list instead of one pair
- Create: `src/WebDataStudio.Server/Services/UserStore.cs` (users, roles, allowed connections)
- Modify: `Endpoints/AuthEndpoints.cs`, `ConnectionRegistry` (filter by the signed-in user)
- Create: `web/src/admin/StudioUsers.tsx`
- Test: `tests/WebDataStudio.Server.Tests/StudioUsersTests.cs`

**Interfaces:**
- Produces: `record StudioUser(string Name, string PasswordHash, string Role, IReadOnlySet<string> Connections)`
  with roles `admin`, `editor`, `viewer`; `WDS_USERS` accepts
  `name:role:bcrypt-hash[:conn,conn]` entries separated by `;`, and `WDS_USER`/`WDS_PASSWORD`
  keep working as a single admin.
- [x] **Step 1: Write the failing tests** — a viewer sees only the connections assigned to them and
      every one of them read-only; an editor may write but not reach the administration endpoints;
      the legacy single-user variables still sign in as admin; an unknown user is rejected in
      constant time.
- [x] **Step 2: Run them** — expect failure.
- [x] **Step 3: Implement** — password hashes verified with `Rfc2898DeriveBytes` (no new
      dependency), role checks as an endpoint filter, connection filtering in the registry, and the
      studio-users tab listing who exists (management stays in the environment: this is deployment
      configuration, not stored state).

      *As built:* `WDS_USERS` takes `name:role:secret[:conn,conn]`, where the secret is either a
      `pbkdf2$iterations$salt$hash` string (`UserStore.Hash`) or a literal password, which is what
      the single-account variables have always been. The connection filter lives in
      `ConnectionRegistry.All()`, so a connection an account may not see does not exist for it on
      any route, and a viewer's connections come back read-only wherever they are opened. The role
      travels in the sign-in cookie (`CurrentUser`), `/api/admin/*` needs admin, `/api/auth/me`
      reports the role and the UI stops offering the administration button without it.
- [x] **Step 4: Run them** — green.
- [x] **Step 5: Commit** — `feat(safety): several studio users, with roles and their own connections`

### Task 4.4: Documentation

- [x] `F20.1`–`F20.3` into the spec and `docs/features.md`; write `docs/guide/safety.md`; extend
      `docs/guide/environment.md` with `WDS_USERS` and the masking variables; link check.
- [x] **Commit** — `docs(safety): masking, undo and studio users`

---

## Phase 5 — The power features (F21)

### Task 5.1: Federation over DuckDB

**Files:**
- Create: `src/WebDataStudio.Server/Services/Federation.cs`
- Create: `src/WebDataStudio.Server/Endpoints/FederationEndpoints.cs` —
  `POST /api/federate/preview`, `POST /api/federate/run`
- Create: `web/src/federate/FederationPanel.tsx`
- Test: `tests/WebDataStudio.Server.Tests/FederationTests.cs`

**Interfaces:**
- Produces: `record FederationSource(string ConnectionId, string Sql, string Alias)`,
  `record FederationRequest(IReadOnlyList<FederationSource> Sources, string Sql, int? MaxRowsPerSource)`;
  the result streams as a normal `ResultChunk` sequence, so the existing grid renders it.

- [x] **Step 1: Write the failing test** — a SQLite table and a PostgreSQL table (Testcontainer)
      joined by a query over `a` and `b` returns the joined rows; a source that exceeds
      `MaxRowsPerSource` (default 100 000) fails with a message naming the source rather than
      filling memory; an unknown alias is a 400.
- [x] **Step 2: Run it** — expect failure.
- [x] **Step 3: Implement** — run each source query, stream its rows into an in-memory DuckDB table
      named by the alias with types derived from the source metadata, then run the federated SQL
      there. One DuckDB connection per request, disposed with it.

      *As built:* rows are staged with batched multi-row `INSERT`s rather than DuckDB's appender —
      fast enough for the row cap and free of type-by-type binding. Types are mapped narrowly
      (numbers stay numbers, dates stay dates, everything else is text), the mask policy of each
      source applies on the way through, and `POST /api/federate/preview` returns the `CREATE
      TABLE` per source without copying a row.
- [x] **Step 4: Run it** — green.
- [x] **Step 5: Commit** — `feat(federation): join across connections by staging in DuckDB`

### Task 5.2: Watch mode

**Files:**
- Modify: `web/src/query/QueryTab.tsx` — a watch toggle with an interval
- Create: `web/src/grid/diffRows.ts`
- Test: `web/src/grid/diffRows.test.ts`

**Interfaces:**
- Produces: `diffRows(previous: unknown[][], next: unknown[][], keyColumns: number[]): RowFlags[]`
  where `RowFlags = "added" | "removed" | "changed" | "same"`.

- [x] **Step 1: Write the failing test** — a changed cell marks the row `changed`, a new key
      `added`, a vanished key `removed`, identical data `same`; without key columns the comparison
      falls back to position.

      *As built:* `diffRows` returns `{flags, cells, removed}` — flags per row of the new result,
      the changed cells as `row:column` for the highlight, and the rows whose key vanished (a
      removed row has no position in the new result, so it cannot be a flag in that list).
      `describeDiff` is what the toolbar says. The highlight is skipped while the grid sorts or
      filters, because then a row's position is no longer the row the diff was about.
- [x] **Step 2: Run it** — expect failure.
- [x] **Step 3: Implement** the diff and the toggle: re-run every N seconds (2, 5, 10, 30), flash
      changed cells in the grid, stop on error and say why, never queue two runs.
- [x] **Step 4: Run it** — green, plus a line in `smoke-grid.mjs` that watches a table while a row
      is inserted.
- [x] **Step 5: Commit** — `feat(query): watch a query and highlight what changed`

### Task 5.3: Test data generator

**Files:**
- Create: `src/WebDataStudio.Server/Services/DataGenerator.cs`
- Modify: `Endpoints/DataEndpoints.cs` — `POST /api/data/{conn}/generate/preview`, `…/apply`
- Create: `web/src/data/GenerateDialog.tsx`
- Test: `tests/WebDataStudio.Server.Tests/DataGeneratorTests.cs`

**Interfaces:**
- Produces: `record GenerateRequest(string ObjectRef, int Rows, IReadOnlyDictionary<string, string>? Strategies, int? Seed)`
  and strategies `auto`, `name`, `email`, `city`, `sentence`, `int`, `decimal`, `date`, `uuid`,
  `boolean`, `fk` (pick an existing key from the referenced table).

- [x] **Step 1: Write the failing test** — generating 50 rows for a table with a foreign key
      produces only values that exist in the parent; a unique column gets distinct values; the same
      seed produces the same rows; the preview is a script that inserts exactly 50 rows.
- [x] **Step 2: Run it** — expect failure.
- [x] **Step 3: Implement** — infer a strategy per column from its name and type, generate with a
      seeded `Random`, respect nullability and length limits, and hand the result to the existing
      preview/apply path.

      *As built:* everything comes from the seed, including dates (counted back from a fixed epoch)
      and UUIDs, so the same seed gives the same rows tomorrow as well. A column the database fills
      in itself is skipped — the identity flag where a driver reports it, plus a lone integer
      primary key, which is a serial, an AUTO_INCREMENT, an IDENTITY or a rowid alias in every
      engine here. `GET /api/data/{conn}/generate/strategies` returns the guess per column so the
      dialog can show and correct it, and the preview names foreign keys with no parent rows.
- [x] **Step 4: Run it** — green.
- [x] **Step 5: Commit** — `feat(data): generate plausible test rows, foreign keys included`

### Task 5.4: Notebooks

**Files:**
- Create: `web/src/notebook/NotebookPanel.tsx`, `web/src/notebook/notebook.ts`
- Modify: `Endpoints/WorkspaceEndpoints.cs` — notebooks are workspace items
- Modify: `web/src/dock/DockShell.tsx` — a `notebook` panel and a palette command
- Test: `web/src/notebook/notebook.test.ts`

**Interfaces:**
- Produces: `type Cell = { id: string; kind: "sql" | "note"; text: string; connectionId?: string }`,
  `toMarkdown(cells: Cell[]): string`, `fromMarkdown(text: string): Cell[]`.

- [x] **Step 1: Write the failing test** — a round trip through Markdown keeps cell order, kind and
      connection; a fenced ```sql block becomes a SQL cell; prose becomes a note.
- [x] **Step 2: Run it** — expect failure.
- [x] **Step 3: Implement** the model, the panel (run a cell with Ctrl+Enter, results under the
      cell, reuse `ResultArea`), saving as a workspace item, and export as Markdown.

      *As built:* no server change was needed — notebooks are stored through the existing workspace
      item endpoints (`notebook:{id}`), which is what saved widths and layouts already use. A cell
      is a plain textarea rather than a Monaco instance: one editor per cell would cost more than
      it gives in a document that is mostly read. A fence that is not `sql` stays prose, so a
      pasted Markdown file keeps its JSON and shell blocks.
- [x] **Step 4: Run it** — green.
- [x] **Step 5: Commit** — `feat(notebook): SQL, prose and results in one saved document`

### Task 5.5: Documentation

- [ ] `F21.1`–`F21.4` into the spec and `docs/features.md`; `docs/guide/federation.md`,
      sections in `docs/guide/results.md` (watch) and `docs/guide/editing.md` (generator);
      link check.
- [ ] **Commit** — `docs(power): federation, watch mode, the generator and notebooks`

---

## Phase 6 — Optional assistance (F22)

Off unless configured: no key, no calls, no mention in the UI.

### Task 6.1: An explain-and-suggest endpoint

**Files:**
- Create: `src/WebDataStudio.Server/Services/Assistant.cs`
- Create: `src/WebDataStudio.Server/Endpoints/AssistantEndpoints.cs` —
  `POST /api/assist/explain`, `POST /api/assist/sql`
- Modify: `web/src/query/QueryTab.tsx` — an "explain" action, shown only when the capability says
  the feature is configured
- Test: `tests/WebDataStudio.Server.Tests/AssistantTests.cs`

**Interfaces:**
- Produces: `record AssistRequest(string ConnectionId, string? Sql, string? Question, bool IncludeSchema)`
  and `record AssistReply(string Text, IReadOnlyList<string> Statements)`; configuration through
  `WDS_ASSIST_ENDPOINT`, `WDS_ASSIST_KEY`, `WDS_ASSIST_MODEL`.

- [ ] **Step 1: Write the failing tests** — without configuration the endpoints answer 501 and
      `/api/health` reports `assist: false`; with a stub endpoint the request carries the schema
      only when `IncludeSchema` is true, and never carries a row of data; the reply's statements are
      returned as text and never executed.
- [ ] **Step 2: Run them** — expect failure.
- [ ] **Step 3: Implement** — a thin HTTP call to an OpenAI-compatible endpoint (`HttpClient`, no
      SDK), a schema summary built from the existing introspection, and a hard rule that nothing
      returned is executed automatically.
- [ ] **Step 4: Run them** — green.
- [ ] **Step 5: Commit** — `feat(assist): optional explain and draft-SQL, off without a key`

### Task 6.2: Documentation

- [ ] `F22.1` into the spec and `docs/features.md`; `docs/guide/assistant.md` stating plainly what
      leaves the machine and what does not; link check.
- [ ] **Commit** — `docs(assist): what it sends, and how to leave it off`

---

## Order and checkpoints

1. Phase 1 (builder) — self-contained, visible immediately.
2. Phase 3 (Redis) — the largest gap against a competitor, independent of everything else.
3. Phase 2 (administration) — builds on the analysis that already exists.
4. Phase 4 (safety) — touches read paths, so it comes after the surfaces that read.
5. Phase 5 (power features) — federation first, it is the differentiator.
6. Phase 6 (assistance) — last, optional, off by default.

After each phase: the full server suite, `npx vitest run`, `npx oxlint src`, every smoke against a
local instance, the link check, and a push that leaves CI green. A phase that cannot be finished
cleanly is left out rather than half-merged, and this file records why.

---

## Phase 7 — The three things that were reported while this plan was running (F23)

Found by reading the code rather than guessing, so each task names what is actually wrong.

### Task 7.1: The data tab's column menu

`web/src/data/DataTab.tsx` renders its own table and its own column menu. Its filter input sits
inside a `Menu.Item` (`DataTab.tsx:201`) — a button, which never hands the focus on; that is the
same defect that was fixed in `ResultGrid`, and it is the only remaining instance in the repository.
The eager `currentTarget` read is already correct there. Two further gaps: every keystroke is a
server round trip (`filter` is in the fetch effect's dependencies, `DataTab.tsx:44-62`) and it resets
the page, and there is no way to hide a column at all.

Reuse of `ResultGrid` was assessed and rejected: it keys column state by numeric index, owns sort and
filter locally, filters N columns at once, and addresses rows by their position in a derived array,
while the data tab pages, sorts and filters on the server, filters exactly one column, and addresses
rows by source index including negative ones for inserted rows. A shared grid would need three
abstractions for two callers. What is shared is the ten lines that caused the bug.

**Files:**
- Create: `web/src/grid/MenuFilterInput.tsx`
- Modify: `web/src/grid/ResultGrid.tsx` (use it), `web/src/data/DataTab.tsx` (use it, debounce,
  hidden columns)
- Create: `web/scripts/smoke-data-menu.mjs`

- [ ] **Step 1:** Write `smoke-data-menu.mjs`: open a table by double-click, open a column menu,
      type into the filter, assert the input holds the keystrokes and the rows narrow; hide a
      column, assert the indicator counts it and restores it.
- [ ] **Step 2:** Run it — fails on the focus.
- [ ] **Step 3:** Extract `MenuFilterInput` (the `div` with `stopPropagation` on click and keydown,
      `data-autofocus`, an eager `currentTarget` read, and a `debounceMs` prop that defaults to 0 so
      `ResultGrid` keeps its current behaviour); use it in both grids with 350 ms in the data tab.
      Add hidden columns to the data tab keyed by column name, with the same eye indicator.
- [ ] **Step 4:** Run the smoke and `smoke-grid`.
- [ ] **Step 5: Commit** — `fix(data): a column menu whose filter can be typed into`

### Task 7.2: An explorer filter that finds tables

The box filters `node.label` of the **first** tree level only (`ExplorerTree.tsx:90-92`, and the
recursion passes `filter=""` at `:138`). Depth 1 is schemas on PostgreSQL, MySQL, Oracle, ClickHouse,
DuckDB and MongoDB, folders on SQLite, database indexes on Redis — never tables. So typing a table
name empties the tree, and typing `tab` matches the folder called "Tables". A matching folder then
shows all of its children unfiltered, which is the opposite of what was wanted.

`web/src/editor/schemaCache.ts` already walks a connection and keeps every table and view with its
schema, and `GoToObject.tsx:24-40` already has a subsequence matcher. Its `invalidate` is never
called by anything.

**Files:**
- Create: `web/src/explorer/fuzzy.ts` (moved out of `GoToObject.tsx`), `web/src/explorer/fuzzy.test.ts`
- Modify: `web/src/explorer/ExplorerTree.tsx`, `web/src/shell/GoToObject.tsx`,
  `web/src/editor/schemaCache.ts` usage on refresh

- [ ] **Step 1:** Write `fuzzy.test.ts`: `matches("ordit", "order_items")` is true,
      `matches("xyz", "order_items")` is false, ranking prefers a prefix hit over a scattered one,
      matching is case-insensitive.
- [ ] **Step 2:** Run it — the module does not exist yet.
- [ ] **Step 3:** Move the matcher into `fuzzy.ts`, use it from `GoToObject`. In the explorer, a
      filter of two characters or more replaces the tree body with the matching tables and views
      from `schemaCache`, each row showing its schema dimmed and keeping select, double-click and
      context menu; an empty box renders today's tree. The refresh button calls
      `schemaCache.invalidate` so the flat list cannot go stale after a DDL change.
- [ ] **Step 4:** Run the tests and `smoke-tree`.
- [ ] **Step 5: Commit** — `fix(explorer): filter for the objects people look for`

### Task 7.3: Panel management — context menu, popout, pinned tabs

dockview 8.1 has all of it already and the shell uses none of it: `DockviewReact` is passed three
props (`DockShell.tsx:902`). `getTabContextMenuItems` returns built-in strings —
`'close' | 'closeOthers' | 'closeAll' | 'closeLeft' | 'closeRight' | 'maximize' | 'float' |
'popout' | 'separator' | 'pin'` — and without the prop there is no context menu at all, which is
today's state. Pinning needs `pinnedTabs: { enabled: true }`, or `setPinned` warns and no-ops.
`addPopoutGroup(item, options)` opens a window at `popoutUrl`, default `/popout.html` — a file this
repository does not have, so the SPA's fallback would serve `index.html` and boot a second copy of
the studio inside the popout. Closing the window re-docks automatically; the group is tracked
through `getPopouts()` and `onDidAddPopoutGroup`/`onDidRemovePopoutGroup`. Styles are copied into the
new document, but Mantine's `data-mantine-color-scheme` attribute is not.

**Files:**
- Create: `web/public/popout.html`
- Modify: `web/src/dock/DockShell.tsx`
- Modify: `web/scripts/smoke-layout.mjs`

- [ ] **Step 1:** Extend `smoke-layout.mjs`: right-click a tab, assert the menu offers Close, Close
      others and Close all; use Close others and assert one tab is left in that group; assert
      `/popout.html` answers 200 and is not the SPA shell.
- [ ] **Step 2:** Run it — no context menu exists.
- [ ] **Step 3:** Add `getTabContextMenuItems`, with `'close'` left out for the explorer and the
      welcome panel so the way back is not lost; `pinnedTabs: { enabled: true }`; an empty
      `popout.html`; and an `onDidOpen` hook that copies `data-mantine-color-scheme` onto the popout
      document so a popped-out panel is not a white rectangle in a dark studio.
- [ ] **Step 4:** Run `smoke-layout` and `smoke`.
- [ ] **Step 5: Commit** — `feat(shell): a tab context menu, pinned tabs and panels that pop out`

### Task 7.4: Documentation

- [ ] `F23.1`–`F23.3` into the spec and `docs/features.md`; a section in `docs/guide/results.md`
      (the data tab's menu), in a new `docs/guide/explorer.md` (searching), and in
      `docs/guide/shortcuts.md` (the tab menu and popout).
- [ ] **Commit** — `docs: the data tab menu, explorer search and panel management`
