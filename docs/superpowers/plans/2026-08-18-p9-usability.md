# P9 — Remaining Usability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close out the feature inventory — connection comfort (SSH tunnels, groups, pooling), editor comfort (parameters, snippets, saved queries, visual query designer), grid comfort (grouping, charts, transpose, result comparison) and shell comfort (command palette, shortcuts, layout presets, deep links).

**Architecture:** No new subsystems. Every item here extends something P0–P8 already built, which is why it lands last.

**Tech Stack:** SSH.NET, the P2 editor and grid, dockview layout serialisation, Mantine Spotlight.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`

## Global Constraints

- Everything from P0–P8 global constraints still holds.
- An SSH private key is a secret: stored through `SecretProtector`, never returned to the client, never logged.
- A layout preset must never be able to strand the user. "Reset layout" is always reachable from the command palette, even when every panel is closed.
- Feature IDs delivered by this phase: F1.4, F1.5, F1.7, F1.8, F3.8, F3.9, F3.11, F3.14, F5.9–F5.12, F13.1, F13.2, F13.4–F13.6.

---

### Task 1: SSH tunnel and TLS options

**Files:**
- Create: `src/WebDataStudio.Server/Services/SshTunnel.cs`
- Create: `src/WebDataStudio.Server/Services/TunnelManager.cs`
- Modify: `src/WebDataStudio.Server/Models/ConnectionSpec.cs`, `SessionFactory.cs`, `ConnectionStore.cs`
- Create: `tests/WebDataStudio.Server.Tests/SshTunnelTests.cs`
- Modify: `web/src/connections/ConnectionForm.tsx`
- Modify: `Directory.Packages.props` (add `SSH.NET`, `Testcontainers` OpenSSH image for the test)

**Interfaces:**
- Produces:
  - `record TunnelSpec(string Host, int Port, string User, string? Password, string? PrivateKey, string? Passphrase)` added to `ConnectionSpec` as a nullable property, encrypted as part of the stored secret blob.
  - `TunnelManager.EnsureAsync(ConnectionSpec, CancellationToken) -> Task<(string Host, int Port)>` — returns the local endpoint the driver should connect to, opening or reusing a tunnel.

- [ ] **Step 1: Write the failing test**

Start a PostgreSQL container on a Docker network reachable only through an OpenSSH container, define a
connection whose tunnel points at the SSH host, and assert the driver connects and reads the seeded
rows. Also assert: a second connection through the same tunnel spec reuses one tunnel; a broken
tunnel reports a connection-level error naming SSH rather than a generic timeout; the private key
never appears in `GET /api/connections`.

- [ ] **Step 2: Implement**

`TunnelManager` keys live tunnels by a hash of the tunnel spec, reference-counts sessions, and closes
a tunnel when its last session ends plus a 60-second grace period. `SessionFactory` calls it before
`driver.OpenAsync` and rewrites host and port in the connection string through the driver's
`DbConnectionStringBuilder`, never by string surgery.

- [ ] **Step 3: Extend the connection form**

An SSH section (host, port, user, password or key, passphrase) and a TLS section (mode, CA
certificate, client certificate and key, verify hostname). Both collapsed by default so the common
case stays a two-field form.

- [ ] **Step 4: Run the test and commit**

```bash
dotnet test --filter SshTunnel
git add -A && git commit -m "feat: SSH tunnels and TLS options for connections"
```

---

### Task 2: Connection groups, colours, pooling and import/export

**Files:**
- Modify: `src/WebDataStudio.Server/Services/ConnectionStore.cs`, `SessionFactory.cs`
- Create: `src/WebDataStudio.Server/Services/SessionPool.cs`
- Create: `tests/WebDataStudio.Server.Tests/SessionPoolTests.cs`
- Modify: `web/src/connections/ConnectionsPage.tsx`, `web/src/explorer/ExplorerTree.tsx`

**Interfaces:**
- Produces:
  - `SessionPool` with `RentAsync(connectionId, ct)` and `Return(session)`, an idle timeout from `WDS_IDLE_TIMEOUT_SECONDS` (default 300), and a per-connection cap from `WDS_MAX_SESSIONS` (default 8).
  - `GET /api/connections/export` and `POST /api/connections/import` — JSON without secrets.

- [ ] **Step 1: Write the failing tests**

Pool: renting twice returns two distinct sessions; returning and renting again reuses one (asserted by
identity); an idle session past the timeout is disposed; the cap blocks the ninth concurrent rent
until one is returned; a broken session is discarded rather than pooled; disposing the pool closes
everything.

Import/export: exporting produces JSON with no `password`, `connectionString` or `privateKey` field;
importing that JSON recreates the connections with empty secrets and marks them as needing
credentials; importing a duplicate name is reported per entry without aborting the rest.

- [ ] **Step 2: Implement and wire**

The explorer groups connections by their `group` field with collapsible headers and tints each
connection's row with its colour — the production-is-red affordance that makes a wrong-window
`DELETE` less likely.

- [ ] **Step 3: Run the tests and commit**

```bash
dotnet test --filter "SessionPool|ConnectionImport"
git add -A && git commit -m "feat: connection groups, colours, pooling and definition import/export"
```

---

### Task 3: Query parameters and snippets

**Files:**
- Create: `web/src/editor/parameters.ts`
- Create: `web/src/editor/parameters.test.ts`
- Create: `web/src/editor/ParameterDialog.tsx`
- Create: `web/src/editor/snippets.ts`
- Create: `web/src/editor/SnippetManager.tsx`
- Modify: `web/src/query/QueryTab.tsx`

**Interfaces:**
- Produces:
  - `findParameters(sql: string, dialect: DialectId): string[]` — the distinct parameter names in order of appearance.
  - `applyParameters(sql, values, dialect): { sql: string; parameters: Record<string, string | null> }`
  - `snippets.ts` with the built-in template list and a user store persisted through `/api/workspace`.

- [ ] **Step 1: Write the failing test**

`findParameters` finds `:name` for PostgreSQL and Oracle, `@name` for SQL Server and MySQL, and
`$name` for SQLite; it ignores a colon inside a string literal, a comment or a PostgreSQL `::` cast;
it deduplicates repeated names while keeping first-appearance order; an empty statement yields none.

- [ ] **Step 2: Implement**

Reuse the P2 scanner's string and comment skipping rather than writing a second one — the `::` cast
case is exactly the kind of thing a naive regex gets wrong.

- [ ] **Step 3: Build the dialog and the snippet manager**

Running a statement with parameters opens a dialog with one field per parameter (remembering the last
values per tab) before execution. The snippet manager offers built-ins (`sel`, `ins`, `upd`, `join`,
`cte`, `idx`) registered as Monaco completion items with tab stops, plus user snippets with a
name/prefix/body editor.

- [ ] **Step 4: Run the test and commit**

```bash
cd web && npx vitest run parameters
git add -A && git commit -m "feat: query parameters and snippets"
```

---

### Task 4: Saved queries

**Files:**
- Modify: `src/WebDataStudio.Server/Services/WorkspaceStore.cs`
- Create: `src/WebDataStudio.Server/Endpoints/SavedQueryEndpoints.cs`
- Create: `tests/WebDataStudio.Server.Tests/SavedQueryTests.cs`
- Create: `web/src/query/SavedQueriesPanel.tsx`

**Interfaces:**
- Produces:
  - `record SavedQuery(string Id, string Name, string? Folder, string Sql, string? ConnectionId, DateTimeOffset UpdatedAt)`
  - `GET/POST/PUT/DELETE /api/saved-queries`

- [ ] **Step 1: Write the failing test**

Create, list, rename, move between folders, update the SQL, delete; listing is sorted by folder then
name; a saved query survives a reopen of the store; deleting a folder's last query leaves no orphan
folder entry.

- [ ] **Step 2: Implement and build the panel**

A tree of folders with drag-to-move, a search box, and actions to open in a new tab, duplicate and
delete. Saving from a query tab offers the tab's current SQL and remembers its connection.

- [ ] **Step 3: Run the test and commit**

```bash
dotnet test --filter SavedQuery
git add -A && git commit -m "feat: saved queries with folders"
```

---

### Task 5: Visual query designer

**Files:**
- Create: `web/src/designer/QueryDesigner.tsx`
- Create: `web/src/designer/buildSelect.ts`
- Create: `web/src/designer/buildSelect.test.ts`

**Interfaces:**
- Produces: `buildSelect(model: QueryModel, dialect: DialectId): string` where `QueryModel` holds
  tables (with aliases), joins (`{ left, right, leftColumn, rightColumn, kind }`), selected columns,
  filters (`{ column, operator, value }`), grouping, aggregates, ordering and a limit.

- [ ] **Step 1: Write the failing test**

One table with two columns produces `SELECT a.x, a.y FROM t a`; a join produces the right `JOIN … ON`
clause with the chosen kind; a filter produces a parameterised `WHERE`; grouping with an aggregate
produces `GROUP BY` listing the non-aggregated columns; ordering and limit use the dialect's paging;
identifiers are quoted per dialect; an empty model produces an empty string rather than invalid SQL.

- [ ] **Step 2: Build the designer**

An `@xyflow/react` canvas where dropping a table from the explorer adds a node with checkboxes per
column, dragging between two column handles creates a join, and a lower panel edits filters,
grouping and ordering. The generated SQL updates live in a read-only Monaco strip and "Open in query
tab" hands it over for editing — one direction only, since round-tripping arbitrary SQL back into the
model is a parser project this phase does not need.

- [ ] **Step 3: Run the test and commit**

```bash
cd web && npx vitest run buildSelect
git add -A && git commit -m "feat: visual query designer"
```

---

### Task 6: Grid grouping, charts, transpose and result comparison

**Files:**
- Create: `web/src/grid/grouping.ts`, `grouping.test.ts`
- Create: `web/src/grid/TransposedView.tsx`
- Create: `web/src/chart/ResultChart.tsx`
- Create: `web/src/chart/inferChart.ts`, `inferChart.test.ts`
- Create: `web/src/compare/ResultCompare.tsx`, `diffResults.ts`, `diffResults.test.ts`
- Modify: `web/src/grid/ResultGrid.tsx`

**Interfaces:**
- Produces:
  - `groupRows(rows, columnIndex): GroupedRow[]` — one level of grouping with per-group counts and numeric subtotals.
  - `inferChart(columns, rows): { kind: "bar" | "line" | "pie"; labelColumn: number; valueColumns: number[] } | null`
  - `diffResults(a, b, keyColumns): { onlyInA, onlyInB, different, identical }`

- [ ] **Step 1: Write the failing tests**

Grouping: rows group by a column's value with correct counts and subtotals; nulls form their own
group; grouping a column of unique values yields one group per row without error.
Chart inference: one text column plus one numeric column suggests a bar chart; a date column plus a
numeric column suggests a line chart; two text columns suggest nothing (`null`); a single numeric
column with few rows suggests a pie chart.
Result diff: identical results diff to nothing; a changed cell lands in `different` naming the column;
rows present on one side only land in the right bucket; a missing key column returns a clear error.

- [ ] **Step 2: Implement the three views**

Charts render with inline SVG rather than a charting dependency: bar, line and pie over one label
column and one or more value columns is a small amount of geometry, and it inherits the Mantine theme
colours for free. The chart type and columns stay user-overridable; inference only picks the default.

- [ ] **Step 3: Wire them into the result area**

A view switcher in the result toolbar: Grid, Form, Transposed, Chart. A "Compare with…" action picks
another open result tab and opens the diff view.

- [ ] **Step 4: Run the tests and commit**

```bash
cd web && npx vitest run grouping inferChart diffResults
git add -A && git commit -m "feat: grid grouping, charts, transposed view and result comparison"
```

---

### Task 7: Command palette, shortcuts, layout presets and deep links

**Files:**
- Create: `web/src/shell/CommandPalette.tsx`
- Create: `web/src/shell/commands.ts`
- Create: `web/src/shell/ShortcutsHelp.tsx`
- Create: `web/src/shell/LayoutPresets.tsx`
- Create: `web/src/shell/deepLink.ts`, `deepLink.test.ts`
- Modify: `web/src/dock/DockShell.tsx`, `web/src/components/AppShellFrame.tsx`

**Interfaces:**
- Consumes: Mantine Spotlight (the AspireUI pattern), `WorkspaceStore` layout slots from P2.
- Produces:
  - `commands.ts` — a single registry `Command { id, label, group, shortcut?, run() }` that both the palette and the shortcut help read, so a command can never appear in one and not the other.
  - `parseDeepLink(url): DeepLink | null` and `buildDeepLink(target): string` for
    `#/c/{connectionId}/o/{objectRef}` and `#/c/{connectionId}/q/{savedQueryId}`.

- [ ] **Step 1: Write the failing test**

`deepLink.test.ts`: an object link round-trips through build and parse; an object reference containing
a slash survives encoding; an unknown path returns `null`; a link with a missing connection id returns
`null`.

- [ ] **Step 2: Build the command registry and palette**

Commands cover: new query tab, run, cancel, format, toggle auto-commit, open connection manager, add
connection, refresh explorer, go to object, open ER diagram, open health report, export result, save
query, switch theme, save layout, reset layout. Ctrl+K opens the palette; the same registry renders
the shortcut help overlay under `?`.

- [ ] **Step 3: Build layout presets**

Save the current dockview layout under a name, per connection or global, list and apply them, and a
Reset action that reloads the default. Presets go through `WorkspaceStore` so they survive a restart.
Reset stays reachable from the palette even with every panel closed — the stranding guard.

- [ ] **Step 4: Wire deep links**

On load, parse the hash and open the referenced object or query; every explorer node and query tab
offers "Copy link".

- [ ] **Step 5: Add job toasts**

Long-running jobs (export, import, backup, restore, deep analyze) report through Mantine
notifications with a progress state and a cancel action where the endpoint supports one.

- [ ] **Step 6: Run the test and commit**

```bash
cd web && npx vitest run deepLink
git add -A && git commit -m "feat: command palette, shortcut help, layout presets and deep links"
```

---

### Task 8: Final sweep

**Files:**
- Modify: `README.md`
- Create: `docs/features.md`
- Create: `tests/WebDataStudio.Server.Tests/FeatureCoverageTests.cs`

- [ ] **Step 1: Write the coverage check**

A test that reads `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`, extracts every feature
id, and asserts each appears in `docs/features.md` with a status of `done` or an explicit
`not-supported: <engine list>`. The spec's inventory stops being a promise and becomes a checked
artefact.

- [ ] **Step 2: Write `docs/features.md`**

One row per feature id: id, description, status, engines it applies to, and where it lives in the UI.

- [ ] **Step 3: Update the README**

Screenshots, the environment variable table, the engine capability matrix from P7, and a short
"what this is" section that names DbGate, DataGrip and phpMyAdmin as the reference points.

- [ ] **Step 4: Run everything**

```bash
dotnet test
cd web && npx vitest run && npm run build
docker build -t webdatastudio:release .
```

- [ ] **Step 5: Commit and tag**

```bash
git add -A && git commit -m "docs: feature coverage matrix and README"
git tag v1.0.0
```

---

## Phase exit criteria

- Every feature id in the spec appears in `docs/features.md` as done or explicitly not supported for
  named engines, and `FeatureCoverageTests` enforces it.
- SSH tunnels connect to a database reachable only through a jump host.
- The command palette reaches every action, and the shortcut help lists the same set.
- Layout presets save, apply and reset without ever stranding the user.
- `docker build` produces the release image and the full test suite is green.
- Feature IDs F1.4, F1.5, F1.7, F1.8, F3.8, F3.9, F3.11, F3.14, F5.9–F5.12, F13.1, F13.2 and
  F13.4–F13.6 are demonstrably working.
