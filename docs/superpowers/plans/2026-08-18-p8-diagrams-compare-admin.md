# P8 — Diagrams, Compare/Deploy, Backup and Administration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The DBA half of the tool — ER diagrams, schema and data diffs with generated sync scripts, backup and restore, user and privilege management, system commands and session control.

**Architecture:** Diagrams reuse the introspection layer and the `@xyflow/react` + `dagre` pairing already in the dependency set. Comparison is a pure function over two `TableDefinition` sets from P6, so it needs no new engine code. Backup shells the engine's own dump tool inside the container; those tools are added to the runtime image in this phase.

**Tech Stack:** `@xyflow/react`, `dagre`, the P6 DDL writers, `pg_dump`/`pg_restore`, `mysqldump`, `sqlcmd`, `mongodump`, `redis-cli`.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`

## Global Constraints

- Everything from P0–P7 global constraints still holds.
- Every administrative action is capability-gated and refused with `NotSupportedException` where the engine cannot do it.
- Backup and restore shell external binaries. Arguments are passed as an argument array, never as a shell string, and the password goes through the environment (`PGPASSWORD`, `MYSQL_PWD`), never the command line where `ps` would show it.
- Restore is destructive. It requires typed confirmation of the target database name and is refused outright on a read-only connection.
- A generated sync script is never executed automatically. It opens in a query tab for the user to read and run.
- Feature IDs delivered by this phase: F4.7, F10.1–F10.3, F11.1–F11.5, F12.1–F12.3.

---

### Task 1: ER diagram

**Files:**
- Create: `src/WebDataStudio.Server/Endpoints/DiagramEndpoints.cs`
- Create: `web/src/diagram/ErDiagram.tsx`
- Create: `web/src/diagram/layout.ts`
- Create: `web/src/diagram/layout.test.ts`
- Create: `web/src/diagram/TableNode.tsx`

**Interfaces:**
- Produces:
  - `GET /api/diagram/{conn}?schema=` → `{ tables: [{ ref, name, columns: [{ name, type, isPrimaryKey }] }], edges: [{ from, to, fromColumns, toColumns, name }] }`
  - `layoutGraph(tables, edges) -> { nodes, edges }` using dagre — pure, testable without a browser.

- [ ] **Step 1: Write the failing test**

`layout.test.ts`: two tables joined by one edge produce two positioned nodes with distinct
coordinates and one edge; an isolated table still gets a position; a self-referencing foreign key
produces a self-edge without crashing the layout; node height scales with the column count.

- [ ] **Step 2: Implement the endpoint**

One introspection pass over the schema's tables, each described once. Cache the result per
connection and schema for 60 seconds — a diagram of 200 tables otherwise re-describes on every pan.

- [ ] **Step 3: Build the diagram**

A custom `TableNode` showing the table name as a header and its columns as rows, with a key icon on
primary keys and a link icon on foreign-key columns. Edges connect column handles, not table centres,
so the relationship is readable. A side panel lists all tables with checkboxes so the user can draw a
subset rather than the whole schema.

- [ ] **Step 4: Add export and navigation**

Export as PNG (canvas render) and SVG (serialise the rendered SVG); both download client-side.
Clicking a table opens its data tab; clicking a column opens the structure panel on it.

- [ ] **Step 5: Verify by hand and commit**

Draw the seeded `people`/`orders` schema, confirm the edge lands on the right columns, export both
formats.

```bash
cd web && npx vitest run layout
git add -A && git commit -m "feat: ER diagram with auto layout and export"
```

---

### Task 2: Schema comparison

**Files:**
- Create: `src/WebDataStudio.Server/Compare/SchemaComparer.cs`
- Create: `src/WebDataStudio.Server/Endpoints/CompareEndpoints.cs`
- Create: `tests/WebDataStudio.Server.Tests/Compare/SchemaComparerTests.cs`
- Create: `web/src/compare/SchemaComparePanel.tsx`

**Interfaces:**
- Consumes: `TableDefinition`, `TableDiff`, `IDdlWriter` from P6.
- Produces:
  - `SchemaComparer.Compare(IReadOnlyList<TableDefinition> source, IReadOnlyList<TableDefinition> target) -> SchemaComparison` with `TablesOnlyInSource`, `TablesOnlyInTarget`, `ChangedTables` (each carrying a `TableChange`), `IdenticalTables`.
  - `POST /api/compare/schema` → the comparison plus a sync script generated with the target's DDL writer.

- [ ] **Step 1: Write the failing tests**

Identical schemas produce an empty comparison; a table present only in the source appears under
`TablesOnlyInSource` and the sync script contains its `CREATE TABLE`; a changed column appears in
`ChangedTables` and the script contains the `ALTER`; a table only in the target produces a `DROP`
marked destructive; the script is generated with the *target* engine's dialect even when the source
is a different engine; comparison ignores column order but not column names.

- [ ] **Step 2: Implement and expose**

`SchemaComparer` is pure over two definition lists; the endpoint fetches both sides through
introspection and hands them over. Cross-engine comparison works because `TableDefinition` uses the
neutral type names P6 introduced.

- [ ] **Step 3: Build the panel**

Two connection/schema pickers, a Compare button, and a three-column result: only-in-source,
changed, only-in-target, each expandable to the column level. The generated script opens in a Monaco
diff editor and in a query tab on demand — never executed from the compare panel itself.

- [ ] **Step 4: Run the tests and commit**

```bash
dotnet test --filter SchemaComparer
git add -A && git commit -m "feat: schema comparison with sync script generation"
```

---

### Task 3: Data comparison

**Files:**
- Create: `src/WebDataStudio.Server/Compare/DataComparer.cs`
- Create: `tests/WebDataStudio.Server.Tests/Compare/DataComparerTests.cs`
- Create: `web/src/compare/DataComparePanel.tsx`

**Interfaces:**
- Produces:
  - `DataComparer.CompareAsync(sourceSession, targetSession, TableDefinition table, IReadOnlyList<string> keyColumns, CancellationToken) -> DataComparison` with `Missing`, `Extra`, `Different` row lists and a `Truncated` flag.
  - `POST /api/compare/data` returning the comparison plus INSERT/UPDATE/DELETE sync statements.

- [ ] **Step 1: Write the failing tests**

Identical tables compare equal; a row missing in the target appears under `Missing` and yields an
INSERT; an extra row yields a DELETE; a differing non-key column yields an UPDATE touching only that
column; comparison requires key columns and returns a clear error without them; a comparison hitting
the row cap sets `Truncated` and says so rather than reporting a partial result as complete.

- [ ] **Step 2: Implement**

Both sides are read ordered by the key columns and walked in lockstep — a merge join, so memory stays
bounded by one row per side rather than by table size. Cap the compared rows at a configurable limit
(default 100 000) and set `Truncated` when it is hit.

- [ ] **Step 3: Build the panel and commit**

Table picker, key column picker (pre-filled from the primary key), a result grid colour-coded by
difference kind, and a generated script that opens in a query tab.

```bash
dotnet test --filter DataComparer
git add -A && git commit -m "feat: data comparison with merge-join walk and sync script"
```

---

### Task 4: Backup and restore

**Files:**
- Create: `src/WebDataStudio.Server/Admin/BackupService.cs`
- Create: `src/WebDataStudio.Server/Admin/ProcessRunner.cs`
- Create: `src/WebDataStudio.Server/Endpoints/BackupEndpoints.cs`
- Create: `tests/WebDataStudio.Server.Tests/Admin/BackupTests.cs`
- Modify: `Dockerfile`
- Create: `web/src/admin/BackupPanel.tsx`

**Interfaces:**
- Produces:
  - `ProcessRunner.RunAsync(string file, IReadOnlyList<string> arguments, IReadOnlyDictionary<string,string> environment, Stream? output, CancellationToken) -> ProcessResult`
  - `BackupService.BackupAsync(ConnectionSpec, BackupOptions, Stream target, CancellationToken)` and `RestoreAsync(ConnectionSpec, Stream source, RestoreOptions, CancellationToken)`
  - `POST /api/admin/backup` (streams the dump as a download), `POST /api/admin/restore` (multipart upload).

- [ ] **Step 1: Add the tools to the runtime image**

Extend the Dockerfile runtime stage with `postgresql-client`, `default-mysql-client`,
`mongodb-database-tools` and `redis-tools`, and note in a comment that each exists only to serve
F11.1. Verify the image still builds for both architectures.

- [ ] **Step 2: Write the failing tests**

`ProcessRunner`: a successful process returns exit code 0 and its stdout; a failing one returns the
exit code and stderr without throwing; a cancelled run kills the process; the password never appears
in the argument list (asserted by inspecting the arguments the service builds).

`BackupService` against the PostgreSQL fixture: a backup produces a non-empty stream containing
`CREATE TABLE`; restoring it into a fresh database recreates the seeded rows; restore against a
read-only connection is refused; backup on an engine without `Caps.Backup` throws `NotSupportedException`.

- [ ] **Step 3: Implement**

Per engine: `pg_dump`/`pg_restore` with `PGPASSWORD`; `mysqldump`/`mysql` with `MYSQL_PWD`;
`BACKUP DATABASE ... TO DISK` for SQL Server (server-side, so the file lands on the server and the
response returns its path rather than a stream); SQLite's `VACUUM INTO` for a consistent file copy;
`mongodump`/`mongorestore`; `redis-cli --rdb`. Anything else declares `Caps.Backup = false`.

- [ ] **Step 4: Build the panel**

Backup: options per engine (schema only, data only, specific tables) and a download. Restore: file
upload, a target picker, a red warning panel, and typed confirmation of the target database name.

- [ ] **Step 5: Run the tests and commit**

```bash
dotnet test --filter "Backup|ProcessRunner"
git add -A && git commit -m "feat: backup and restore through the engines' own dump tools"
```

---

### Task 5: Users, privileges, sessions and system commands

**Files:**
- Create: `src/WebDataStudio.Server/Admin/UserAdminService.cs`
- Create: `src/WebDataStudio.Server/Admin/SessionService.cs`
- Create: `src/WebDataStudio.Server/Admin/SystemCommandCatalog.cs`
- Create: `src/WebDataStudio.Server/Endpoints/AdminEndpoints.cs`
- Create: `tests/WebDataStudio.Server.Tests/Admin/AdminTests.cs`
- Create: `web/src/admin/UsersPanel.tsx`, `SessionsPanel.tsx`, `SystemCommandsPanel.tsx`, `DatabasesPanel.tsx`

**Interfaces:**
- Produces:
  - `GET /api/admin/users/{conn}`, `POST /api/admin/users/{conn}` (create, drop, grant, revoke — each previewed as SQL first)
  - `GET /api/admin/sessions/{conn}`, `POST /api/admin/sessions/{conn}/{id}/kill`
  - `GET /api/admin/system-commands/{conn}` → the catalogue this engine supports, `POST /api/admin/system-command/{conn}`
  - `GET /api/admin/databases/{conn}`, `POST`/`DELETE` for create and drop

- [ ] **Step 1: Write the failing tests**

Users: listing returns the seeded users per engine; creating a user emits the correct statement per
dialect and is previewed before it runs; granting and revoking round-trip. Sessions: the list contains
the test's own connection; killing a session created by the test terminates it; killing on an engine
without `Caps.KillSession` returns 400. System commands: the catalogue for PostgreSQL contains VACUUM,
ANALYZE and REINDEX and for SQL Server contains DBCC CHECKDB; running one against the seeded database
succeeds; an unlisted command is rejected (the catalogue is an allow-list, not a passthrough).
Databases: create and drop work where `Caps.MultiDatabase`.

- [ ] **Step 2: Implement**

`SystemCommandCatalog` is a static per-engine list of `record SystemCommand(string Id, string Label, string Sql, bool NeedsTarget, bool Destructive)`. Execution substitutes the target through
`dialect.QuoteIdentifier` — the endpoint never accepts raw SQL, which is what keeps this feature from
being a second, unlogged query console.

Sessions per engine: `pg_stat_activity` plus `pg_terminate_backend`; `information_schema.processlist`
plus `KILL`; `sys.dm_exec_sessions` plus `KILL`; MongoDB `currentOp` plus `killOp`; Redis
`CLIENT LIST` plus `CLIENT KILL`.

- [ ] **Step 3: Build the panels**

Users: a table with create, drop and a privilege matrix; every action previews its SQL first.
Sessions: a table with the query text, duration, blocking session, and a kill action behind a
confirmation. System commands: cards from the catalogue with a target picker and a destructive
warning where flagged. Databases: list, create, drop with typed confirmation.

- [ ] **Step 4: Run the tests and commit**

```bash
dotnet test --filter Admin
git add -A && git commit -m "feat: user, session, system command and database administration"
```

---

### Task 6: Server log viewer

**Files:**
- Create: `src/WebDataStudio.Server/Admin/ServerLogService.cs`
- Create: `web/src/admin/ServerLogPanel.tsx`

**Interfaces:**
- Produces: `GET /api/admin/logs/{conn}?lines=` where the engine exposes logs — PostgreSQL through
  `pg_read_file` on `log_directory` when the role permits, MySQL through
  `performance_schema.error_log`, SQL Server through `sys.xp_readerrorlog`. Everything else reports
  that logs are not reachable, which is the honest answer for a remote managed database.

- [ ] **Step 1: Implement with a clear unavailable state**

The panel must distinguish "this engine cannot expose logs", "this engine can but this role lacks the
permission", and "here are the logs". Three states, three messages.

- [ ] **Step 2: Commit**

```bash
git add -A && git commit -m "feat: server log viewer where the engine allows it"
```

---

## Phase exit criteria

- An ER diagram renders a real schema with correct column-level edges and exports to PNG and SVG.
- Schema and data comparison produce sync scripts that, when run, make the target match the source —
  verified end to end against two live databases.
- Backup and restore round-trip a PostgreSQL database inside the container, with no password on any
  command line.
- Users, sessions, system commands and databases are manageable for every engine that declares the
  capability and hidden for the rest.
- Feature IDs F4.7, F10.1–F10.3, F11.1–F11.5 and F12.1–F12.3 are demonstrably working.
