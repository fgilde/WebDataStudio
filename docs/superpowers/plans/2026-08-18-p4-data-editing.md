# P4 — Data Editing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Edit table data in the grid like a spreadsheet, see exactly which SQL that will run before it runs, and navigate foreign keys between tables.

**Architecture:** Edits accumulate client-side as a change set keyed by primary key. The server turns a change set into a script (`preview-changes`), and only a separately confirmed call executes it inside one transaction. Nothing is written without a preview the user has seen.

**Tech Stack:** the P1 driver layer, the P2 grid, Mantine modals, Monaco diff view for the preview.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`

## Global Constraints

- Everything from P0–P3 global constraints still holds.
- Preview is mandatory. `apply-changes` accepts only a change set that was previewed in the same session and whose hash matches; a mismatch is a 409.
- A table without a primary key or unique index is not editable. The UI says why instead of failing at apply time.
- Every apply runs in one transaction where the engine supports transactions; on any statement failure the whole set rolls back and the error names the offending row.
- Feature IDs delivered by this phase: F4.5, F5.8, F6.1–F6.6.

---

### Task 1: Row identity and the change-set model

**Files:**
- Create: `src/WebDataStudio.Server/Editing/ChangeSet.cs`
- Create: `src/WebDataStudio.Server/Editing/RowIdentity.cs`
- Create: `tests/WebDataStudio.Server.Tests/Editing/RowIdentityTests.cs`

**Interfaces:**
- Consumes: `ObjectDetail`, `ColumnInfo`, `IndexInfo` from P1.
- Produces:
  - `RowIdentity.Resolve(ObjectDetail) -> IdentityResult` with `bool Editable`, `IReadOnlyList<string> KeyColumns`, `string? Reason`.
  - `record RowChange(string Kind, IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?> Values)` where `Kind` is `insert`, `update` or `delete`.
  - `record ChangeSet(string ConnectionId, string ObjectRef, IReadOnlyList<RowChange> Changes)` with `string Hash()` (SHA-256 over the canonical JSON) used by the preview/apply handshake.

- [ ] **Step 1: Write the failing test**

Assert: a table with a primary key resolves to editable with those key columns; a table with no
primary key but a unique non-nullable index resolves to editable using that index; a table with
neither resolves to not editable with a reason naming the missing key; a view resolves to not
editable; the change-set hash is stable across property ordering and changes when a value changes.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter RowIdentity`

- [ ] **Step 3: Implement both types**

`RowIdentity` prefers the primary key, then the first unique index whose columns are all
non-nullable, then reports why editing is off. Canonical JSON for the hash sorts object keys so two
semantically equal change sets hash identically.

- [ ] **Step 4: Run the test and commit**

```bash
dotnet test --filter RowIdentity
git add -A && git commit -m "feat: row identity resolution and change-set model"
```

---

### Task 2: Change-script builder

**Files:**
- Create: `src/WebDataStudio.Server/Editing/ChangeScriptBuilder.cs`
- Create: `tests/WebDataStudio.Server.Tests/Editing/ChangeScriptBuilderTests.cs`

**Interfaces:**
- Consumes: `SqlDialect`, `ChangeSet`, `ObjectDetail`.
- Produces: `ChangeScriptBuilder.Build(ChangeSet, ObjectDetail, SqlDialect) -> ChangeScript` with
  `IReadOnlyList<ScriptStatement> Statements` and `string Text`, where
  `record ScriptStatement(string Sql, IReadOnlyDictionary<string, object?> Parameters, int ChangeIndex)`.

- [ ] **Step 1: Write the failing tests**

Assert per dialect: an update produces `UPDATE <quoted> SET col = @p0 WHERE key = @p1` with only the
changed columns in SET; an insert lists exactly the supplied columns; a delete matches on every key
column; a composite key produces one predicate per key column joined with AND; a null value becomes
a parameter carrying `DBNull`, never the literal string `null`; the generated `Text` is fully
parameter-substituted and readable (it is shown to the user, while execution uses the parameters);
statements come out in the order delete, update, insert so a delete-then-reinsert of the same key
cannot collide.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter ChangeScriptBuilder`

- [ ] **Step 3: Implement the builder**

Identifiers go through `dialect.QuoteIdentifier`, values through parameters, and the preview text
through the same `Literal()` helper P3's SQL exporter uses — one renderer, so the preview cannot
drift from what actually runs.

- [ ] **Step 4: Run the tests and commit**

```bash
dotnet test --filter ChangeScriptBuilder
git add -A && git commit -m "feat: change-script builder"
```

---

### Task 3: Data browse and edit endpoints

**Files:**
- Create: `src/WebDataStudio.Server/Endpoints/DataEndpoints.cs`
- Create: `src/WebDataStudio.Server/Services/PendingChangeCache.cs`
- Create: `tests/WebDataStudio.Server.Tests/Editing/DataEndpointTests.cs`
- Modify: `src/WebDataStudio.Server/Program.cs`

**Interfaces:**
- Consumes: `SessionFactory`, `ChangeScriptBuilder`, `RowIdentity`.
- Produces:
  - `GET /api/data/{conn}/{ref}?offset=&limit=&sort=&filter=` → `{ columns, rows, editable, keyColumns, reason, totalEstimate }`
  - `POST /api/data/{conn}/{ref}/preview-changes` → `{ hash, script, statementCount, affectedRows }`
  - `POST /api/data/{conn}/{ref}/apply-changes` → `{ applied, failedAt, error }`
  - `GET /api/data/{conn}/{ref}/lookup?column=&search=` → foreign-key lookup values.

- [ ] **Step 1: Write the failing tests**

Browse returns paged rows honouring offset and limit through `dialect.Paginate`; browse of a
key-less table returns `editable: false` with a reason; preview returns a script and a hash without
touching the data (asserted by re-reading the row); apply with the matching hash writes the change;
apply with a stale hash returns 409; apply on a read-only connection returns 403; a failing
statement in the middle rolls the whole set back (asserted by re-reading both rows).

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter DataEndpoint`

- [ ] **Step 3: Implement `PendingChangeCache`**

An in-memory `MemoryCache` keyed by change-set hash with a 10-minute sliding expiry, holding the
built script. Apply looks the hash up rather than rebuilding, so what executes is byte-for-byte what
the user approved.

- [ ] **Step 4: Implement the endpoints**

Browse builds `SELECT * FROM <ref>` plus optional ORDER BY and WHERE from the query parameters, all
identifiers quoted through the dialect and all filter values parameterised — filters come from the
client and must never be concatenated. Apply opens a transaction when
`driver.Caps.Transactions`, executes each statement, and commits; on failure it rolls back and
returns the index of the failed change.

- [ ] **Step 5: Run the tests and commit**

```bash
dotnet test --filter DataEndpoint
git add -A && git commit -m "feat: data browse and preview/apply editing endpoints"
```

---

### Task 4: Grid editing

**Files:**
- Create: `web/src/grid/editing/useChangeSet.ts`
- Create: `web/src/grid/editing/useChangeSet.test.ts`
- Create: `web/src/grid/editing/EditableCell.tsx`
- Create: `web/src/grid/editing/ChangePreviewModal.tsx`
- Modify: `web/src/grid/ResultGrid.tsx`

**Interfaces:**
- Consumes: the data endpoints from Task 3.
- Produces:
  - `useChangeSet(keyColumns, columns)` returning `{ changes, edit(rowIndex, column, value), insertRow(), deleteRow(rowIndex), duplicateRow(rowIndex), revert(rowIndex), revertAll(), isDirty, cellState(rowIndex, column) }` where `cellState` is `"clean" | "edited" | "inserted" | "deleted"`.

- [ ] **Step 1: Write the failing test**

Assert: editing a cell records one update carrying only that column; editing the same cell twice
keeps one change with the latest value; editing a cell back to its original value drops the change
entirely; deleting an inserted row removes the insert instead of recording a delete; duplicating a
row produces an insert with the key columns cleared; `revertAll` empties the set.

- [ ] **Step 2: Run it to verify it fails**

Run: `cd web && npx vitest run useChangeSet`

- [ ] **Step 3: Implement the hook and the cell**

`EditableCell` switches to an input on double-click or Enter, commits on blur or Enter, cancels on
Escape, and offers a type-appropriate editor: a `Switch` for booleans, a date picker for dates, a
`Textarea` in a popover for long text and JSON, and a "set NULL" action in every editor so a value
can be cleared without ambiguity. Dirty cells get a left border in the theme's accent colour;
deleted rows get strikethrough.

- [ ] **Step 4: Build the preview modal**

Shows the script from `preview-changes` in a read-only Monaco editor with SQL highlighting, the
statement count and the estimated affected rows, and two buttons: Apply and Cancel. Apply sends the
hash. On success the grid reloads the affected page; on 409 it re-previews and tells the user the
data changed underneath.

- [ ] **Step 5: Wire the toolbar**

Save (preview then apply), Revert all, Insert row, Delete row, Duplicate row, and an
auto-commit toggle plus explicit Commit and Rollback buttons when `caps.Transactions` (F4.5). The
Save button is disabled with a tooltip naming the reason when the table is not editable.

- [ ] **Step 6: Verify by hand**

Edit two cells and insert a row against the SQLite demo table, read the preview, apply, and confirm
the values changed; try the same against a key-less view and confirm the clear refusal.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: spreadsheet-style grid editing with mandatory change preview"
```

---

### Task 5: Foreign-key navigation and lookups

**Files:**
- Create: `web/src/grid/ForeignKeyLink.tsx`
- Create: `web/src/grid/editing/LookupSelect.tsx`
- Modify: `web/src/grid/ResultGrid.tsx`, `web/src/query/ResultArea.tsx`

**Interfaces:**
- Consumes: `ObjectDetailDto.foreignKeys`, `GET /api/data/{conn}/{ref}/lookup`.
- Produces: `<ForeignKeyLink>` (F5.8) and `<LookupSelect>` (F6.5).

- [ ] **Step 1: Render foreign-key cells as links**

A cell whose column participates in a foreign key gets a subtle arrow affordance; clicking it opens a
new data tab on the referenced table filtered to the matching key, so the user lands on the single
target row rather than the whole table.

- [ ] **Step 2: Build the lookup editor**

Editing a foreign-key column shows a searchable `Select` fed by
`/api/data/{conn}/{ref}/lookup?column=&search=`, which returns the referenced key plus the first
text-like column as a label, capped at 50 rows per search so a million-row lookup table stays usable.

- [ ] **Step 3: Add master-detail**

When a table has incoming foreign keys, the result area gains a Detail pane listing child rows of the
selected parent row, refreshed on selection change.

- [ ] **Step 4: Verify by hand and commit**

Click through from `orders.person_id` to the matching `people` row, and edit `orders.person_id`
through the lookup dropdown.

```bash
git add -A && git commit -m "feat: foreign-key navigation, lookup editing and master-detail"
```

---

### Task 6: Bulk update over a selection

**Files:**
- Create: `web/src/grid/editing/BulkUpdateModal.tsx`
- Create: `web/src/grid/editing/applyMacro.ts`
- Create: `web/src/grid/editing/applyMacro.test.ts`

**Interfaces:**
- Consumes: `useChangeSet`.
- Produces: `applyMacro(value: unknown, macro: Macro): unknown` where `Macro` is one of
  `{ kind: "set"; value: string }`, `{ kind: "null" }`, `{ kind: "trim" }`, `{ kind: "upper" }`,
  `{ kind: "lower" }`, `{ kind: "replace"; find: string; with: string; regex: boolean }`,
  `{ kind: "add"; amount: number }`, `{ kind: "template"; pattern: string }`.

- [ ] **Step 1: Write the failing test**

Assert each macro against a representative value, plus: `add` on a non-numeric value leaves it
unchanged rather than producing `NaN`; `replace` with an invalid regex reports an error instead of
throwing; `template` substitutes `{value}` and `{row}`.

- [ ] **Step 2: Implement and wire**

The modal previews the first ten transformed values next to their originals before writing anything
into the change set — the same "see it before it happens" rule as the SQL preview.

- [ ] **Step 3: Run the test and commit**

```bash
cd web && npx vitest run applyMacro
git add -A && git commit -m "feat: bulk update macros over a grid selection"
```

---

## Phase exit criteria

- Editing a cell, inserting, duplicating and deleting rows all produce a preview that matches what
  executes, verified against a live database.
- A table without a usable key reports why it cannot be edited instead of failing at apply time.
- A mid-set failure rolls the whole change set back.
- Foreign-key cells navigate to the referenced row, and editing one offers a searchable lookup.
- Explicit transaction control works for engines that support it.
- Feature IDs F4.5, F5.8 and F6.1–F6.6 are demonstrably working.
