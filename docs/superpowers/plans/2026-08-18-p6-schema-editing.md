# P6 — Schema Editing and DDL Designer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create and change tables, columns, indexes, constraints, views, procedures, functions and triggers from the UI — with the migration script shown and confirmed before anything runs.

**Architecture:** A per-engine `IDdlWriter` turns a declarative `TableDefinition` and a diff between two definitions into statements. The designer edits a definition client-side; saving diffs it against the loaded original and previews the resulting script. Same rule as P4: nothing executes that the user has not seen.

**Tech Stack:** the P1 driver layer, the P4 preview handshake, Monaco diff editor, Mantine.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`

## Global Constraints

- Everything from P0–P5 global constraints still holds.
- Every schema change goes through preview-then-apply with a hash handshake, reusing P4's `PendingChangeCache`.
- Destructive steps are labelled as such in the preview. A script containing a `DROP` requires the user to type the object name to confirm.
- Where an engine cannot express a change in place (SQLite altering a column type), the writer emits the table-rebuild sequence explicitly rather than pretending the simple statement works.
- A rename shows every dependent object before it runs.
- Feature IDs delivered by this phase: F8.1–F8.7.

---

### Task 1: Table definition model and diff

**Files:**
- Create: `src/WebDataStudio.Server/Ddl/TableDefinition.cs`
- Create: `src/WebDataStudio.Server/Ddl/TableDiff.cs`
- Create: `tests/WebDataStudio.Server.Tests/Ddl/TableDiffTests.cs`

**Interfaces:**
- Consumes: `ObjectDetail` from P1.
- Produces:
  - `record TableDefinition(string Schema, string Name, IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<IndexDefinition> Indexes, IReadOnlyList<ConstraintDefinition> Constraints, string? Comment)` plus `TableDefinition.From(ObjectDetail)`.
  - `record ColumnDefinition(string Name, string Type, bool Nullable, string? Default, bool Identity, string? Comment, string? RenamedFrom)`
  - `TableDiff.Compute(TableDefinition before, TableDefinition after) -> TableChange` listing added, dropped, altered and renamed columns, added and dropped indexes and constraints, and a changed comment.

- [ ] **Step 1: Write the failing tests**

Assert: an unchanged definition produces an empty diff; adding a column appears as added only;
removing one appears as dropped; changing a type appears as altered with both types recorded;
a column carrying `RenamedFrom` appears as a rename, not as a drop plus an add; changing nullability
and default on the same column produces one altered entry carrying both; index and constraint diffs
compare by column list, not by name, so a renamed index with identical columns is not reported as a
drop plus an add.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter TableDiff`

- [ ] **Step 3: Implement the model and the diff**

`TableDiff` is pure and engine-independent; every engine-specific decision lives in the writer.

- [ ] **Step 4: Run the tests and commit**

```bash
dotnet test --filter TableDiff
git add -A && git commit -m "feat: table definition model and engine-independent diff"
```

---

### Task 2: DDL writers

**Files:**
- Create: `src/WebDataStudio.Server/Ddl/IDdlWriter.cs`
- Create: `src/WebDataStudio.Server/Drivers/PostgreSql/PostgreSqlDdlWriter.cs`
- Create: `src/WebDataStudio.Server/Drivers/MySql/MySqlDdlWriter.cs`
- Create: `src/WebDataStudio.Server/Drivers/SqlServer/SqlServerDdlWriter.cs`
- Create: `src/WebDataStudio.Server/Drivers/Sqlite/SqliteDdlWriter.cs`
- Create: `tests/WebDataStudio.Server.Tests/Ddl/DdlWriterContractTests.cs`

**Interfaces:**
- Consumes: `TableDefinition`, `TableChange`, `SqlDialect`.
- Produces:
  - `interface IDdlWriter { IReadOnlyList<DdlStatement> CreateTable(TableDefinition t); IReadOnlyList<DdlStatement> AlterTable(TableDefinition before, TableChange change); IReadOnlyList<DdlStatement> DropTable(string schema, string name); IReadOnlyList<DdlStatement> CreateIndex(IndexDefinition i); IReadOnlyList<DdlStatement> DropIndex(string schema, string table, string name); IReadOnlyList<DdlStatement> Rename(SchemaNodeRef target, string newName); IReadOnlyList<DdlStatement> CreateOrReplaceRoutine(RoutineDefinition r); }`
  - `record DdlStatement(string Sql, bool Destructive, string Description)` — `Destructive` drives the confirmation gate.
- Each driver exposes its writer through `IDbDriver.Ddl`.

- [ ] **Step 1: Write the contract tests**

A shared suite over all four writers asserting: `CreateTable` quotes every identifier with the
engine's own quoting; a nullable column omits `NOT NULL` and a non-nullable one includes it; the
primary key appears either inline or as a constraint but exactly once; adding a column produces an
`ALTER TABLE ... ADD`; dropping one is marked `Destructive`; renaming a column uses the engine's
syntax (`ALTER TABLE ... RENAME COLUMN` for PostgreSQL and SQLite, `CHANGE` for older MySQL,
`sp_rename` for SQL Server); a type change on SQLite produces the four-statement rebuild sequence
(create new, copy, drop old, rename) rather than an unsupported `ALTER COLUMN TYPE`; a partial index
is emitted only where `Caps.PartialIndexes`; `INCLUDE` columns only where `Caps.IncludeColumns`.

Additionally: every writer's output must round-trip — creating the table from a definition, reading it
back with `DescribeAsync`, and converting to a definition again yields an empty diff. That test runs
against the live tier-1 fixtures and is the strongest guarantee the writers are correct.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter DdlWriter`

- [ ] **Step 3: Implement the four writers**

Each keeps a `TypeMap` from the neutral type names the designer offers (`text`, `int`, `bigint`,
`decimal(p,s)`, `bool`, `timestamp`, `date`, `uuid`, `json`, `blob`) to the engine's own, plus the
reverse direction for reading a definition back. An unmapped type passes through verbatim with a
warning in the statement description.

- [ ] **Step 4: Run the tests and commit**

```bash
dotnet test --filter DdlWriter
git add -A && git commit -m "feat: DDL writers for the tier-1 engines"
```

---

### Task 3: Dependency lookup

**Files:**
- Create: `src/WebDataStudio.Server/Ddl/DependencyFinder.cs`
- Modify: each tier-1 driver
- Create: `tests/WebDataStudio.Server.Tests/Ddl/DependencyTests.cs`

**Interfaces:**
- Produces: `IDbDriver.FindDependenciesAsync(session, target, ct) -> DependencyReport` with
  `IReadOnlyList<SchemaNodeRef> DependsOn` and `IReadOnlyList<SchemaNodeRef> UsedBy`, and the endpoint
  `GET /api/schema/{conn}/object/{ref}/dependencies` (declared in the spec, wired here).

- [ ] **Step 1: Write the failing test**

Seed a view over `people` and a foreign key from `orders`. Assert that `people`'s `UsedBy` contains
both the view and `orders`, and that the view's `DependsOn` contains `people`.

- [ ] **Step 2: Implement per engine**

PostgreSQL: `pg_depend` joined to `pg_rewrite` for views plus `pg_constraint` for foreign keys.
MySQL: `information_schema.view_table_usage` where available, otherwise a text match against
`view_definition`, plus `key_column_usage`. SQL Server: `sys.sql_expression_dependencies` plus
`sys.foreign_keys`. SQLite: parse `sqlite_master.sql` for references — documented as best-effort.

- [ ] **Step 3: Run the test and commit**

```bash
dotnet test --filter Dependency
git add -A && git commit -m "feat: object dependency lookup"
```

---

### Task 4: Schema-change endpoints

**Files:**
- Create: `src/WebDataStudio.Server/Endpoints/DdlEndpoints.cs`
- Create: `tests/WebDataStudio.Server.Tests/Ddl/DdlEndpointTests.cs`

**Interfaces:**
- Consumes: `IDdlWriter`, `PendingChangeCache` from P4.
- Produces:
  - `GET /api/ddl/{conn}/{ref}` → the current `TableDefinition` plus the engine's own `CREATE` text.
  - `POST /api/ddl/{conn}/preview` → `{ hash, statements: [{ sql, destructive, description }] }`
  - `POST /api/ddl/{conn}/apply` → executes a previewed hash in one transaction where supported.
  - `POST /api/ddl/{conn}/rename` → preview including the dependency report.
  - `POST /api/ddl/{conn}/routine` → create or replace a view, procedure, function or trigger from raw text.

- [ ] **Step 1: Write the failing tests**

Preview of an added column returns one non-destructive statement and writes nothing; apply with the
hash creates the column, verified by re-describing the table; apply with a stale hash returns 409;
preview of a dropped column marks the statement destructive; apply on a read-only connection returns
403; a failing statement mid-script rolls back everything for engines with transactional DDL and, for
those without, the response names exactly which statements already ran.

- [ ] **Step 2: Implement**

The non-transactional-DDL case matters: MySQL commits each DDL statement implicitly. The response
carries `partiallyApplied: true` plus the executed statement list so the user knows the real state
rather than believing in a rollback that never happened.

- [ ] **Step 3: Run the tests and commit**

```bash
dotnet test --filter DdlEndpoint
git add -A && git commit -m "feat: DDL preview and apply endpoints"
```

---

### Task 5: Table designer UI

**Files:**
- Create: `web/src/designer/TableDesigner.tsx`
- Create: `web/src/designer/ColumnsEditor.tsx`
- Create: `web/src/designer/IndexesEditor.tsx`
- Create: `web/src/designer/ConstraintsEditor.tsx`
- Create: `web/src/designer/MigrationPreview.tsx`
- Create: `web/src/designer/definition.ts`
- Create: `web/src/designer/definition.test.ts`

**Interfaces:**
- Consumes: the endpoints from Task 4.
- Produces:
  - `definition.ts` with `emptyDefinition(schema)`, `addColumn(def)`, `removeColumn(def, index)`, `renameColumn(def, index, name)`, `moveColumn(def, from, to)` — all pure, all tested.
  - `<TableDesigner connectionId objectRef?>` — a dockview panel; without an `objectRef` it designs a new table.

- [ ] **Step 1: Write the failing test**

`definition.test.ts`: renaming a column records `renamedFrom` on first rename and keeps the original
value on a second rename (so a rename chain still diffs correctly against the database); removing a
newly added column leaves no trace; moving a column reorders without losing properties.

- [ ] **Step 2: Build the columns editor**

A table of rows with: name, type (a combobox of the neutral types plus free text), length/precision,
nullable, default, identity, comment, and row actions for move up/down and delete. New rows append at
the end; deleted existing rows are struck through until saved.

- [ ] **Step 3: Build the indexes and constraints editors**

Indexes: name, column list (multi-select with order), unique, and — capability-gated — partial
predicate and include columns. Constraints: primary key, unique, check with an expression field, and
foreign key with target table, target columns and the ON DELETE/ON UPDATE actions.

- [ ] **Step 4: Build the migration preview**

The Save button calls preview, then shows the statements in a Monaco diff editor: current DDL on the
left, the resulting DDL on the right, with the statement list underneath. Destructive statements are
flagged in red, and a script containing any of them requires typing the table name to enable Apply.

- [ ] **Step 5: Add the routine editors**

A Monaco panel for views, procedures, functions and triggers: loads the current source from
`ObjectDetail.Ddl`, saves via `POST /api/ddl/{conn}/routine`, and shows the server's error mapped to
the right line on failure.

- [ ] **Step 6: Add rename with dependency warning**

The explorer's Rename action opens a dialog showing the dependency report before it previews the
statement, so a rename that will break three views says so first.

- [ ] **Step 7: Verify by hand**

Against PostgreSQL and SQLite: create a table with two columns, an index and a foreign key from the
designer; add a column; change a column type on SQLite and confirm the rebuild sequence appears in
the preview and works; drop a column and confirm the typed confirmation is required.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "feat: table designer, routine editors and migration preview"
```

---

## Phase exit criteria

- A table created from the designer round-trips: reading it back and diffing yields no changes, for
  every tier-1 engine.
- Adding, altering, renaming and dropping columns, indexes and constraints all work through
  preview-then-apply.
- SQLite type changes go through the explicit rebuild sequence and the preview shows it.
- Destructive scripts require typed confirmation; renames show dependencies first.
- Engines without transactional DDL report exactly what ran when a script fails midway.
- Feature IDs F8.1–F8.7 are demonstrably working.
