# P2 — Query Editor and Result Grid Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write SQL in Monaco with schema-aware completion, run the selection or the statement under the cursor, and read the result in a virtualised grid — the point at which the tool becomes usable daily.

**Architecture:** A TypeScript port of the server's statement splitter drives both the "execute selection" behaviour and the active-statement highlight. Query execution reads the NDJSON stream incrementally so rows appear while the query is still running. The grid is TanStack Table (headless) over TanStack Virtual, styled with Mantine.

**Tech Stack:** monaco-editor, @tanstack/react-table, @tanstack/react-virtual, sql-formatter, dockview, Mantine 9, Vitest.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`

## Global Constraints

- Everything from P0 and P1 global constraints still holds.
- Monaco's theme follows the active `AppTheme.monaco` value; switching a theme must restyle the editor without a reload.
- The grid must stay responsive with 100k rows in memory: virtualise rows and columns, never render the full set.
- Query history and open tabs live on the server so they survive a container restart, not only a page reload.
- Feature IDs delivered by this phase: F3.1–F3.7, F3.10, F3.12, F3.13, F4.8, F5.1–F5.7.

---

### Task 1: TypeScript statement splitter

**Files:**
- Create: `web/src/sql/splitStatements.ts`
- Create: `web/src/sql/splitStatements.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: `splitStatements(sql: string, dialect: DialectId): SqlStatement[]` where
  `interface SqlStatement { text: string; start: number; end: number }` (character offsets into the
  original string) and `type DialectId = "postgresql" | "mysql" | "sqlserver" | "sqlite" | "oracle" | "duckdb" | "clickhouse"`.
  Also `statementAt(sql, offset, dialect): SqlStatement | null`.

- [ ] **Step 1: Write the failing tests**

`web/src/sql/splitStatements.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { splitStatements, statementAt } from "./splitStatements";

const texts = (sql: string, dialect = "postgresql" as const) =>
  splitStatements(sql, dialect).map(s => s.text.trim());

describe("splitStatements", () => {
  it("splits on semicolons", () => expect(texts("SELECT 1; SELECT 2;")).toEqual(["SELECT 1", "SELECT 2"]));
  it("ignores a semicolon in a string", () => expect(texts("SELECT 'a;b'")).toHaveLength(1));
  it("ignores a semicolon in a line comment", () => expect(texts("SELECT 1 -- a;b\n")).toHaveLength(1));
  it("ignores a semicolon in a block comment", () => expect(texts("SELECT /* a;b */ 1")).toHaveLength(1));
  it("keeps a dollar-quoted body intact", () =>
    expect(texts("CREATE FUNCTION f() AS $$ SELECT 1; $$ LANGUAGE sql;")).toHaveLength(1));
  it("splits sql server batches on GO", () =>
    expect(texts("SELECT 1\nGO\nSELECT 2", "sqlserver" as never)).toEqual(["SELECT 1", "SELECT 2"]));
  it("reports character offsets", () => {
    const [first, second] = splitStatements("SELECT 1;\nSELECT 2;", "postgresql");
    expect(first.start).toBe(0);
    expect(second.start).toBeGreaterThan(first.end);
  });
});

describe("statementAt", () => {
  const sql = "SELECT 1;\nSELECT 2;";
  it("finds the statement containing the cursor", () =>
    expect(statementAt(sql, 12, "postgresql")?.text.trim()).toBe("SELECT 2"));
  it("returns the preceding statement when the cursor sits on the terminator", () =>
    expect(statementAt(sql, 9, "postgresql")?.text.trim()).toBe("SELECT 1"));
  it("returns null for empty input", () => expect(statementAt("   ", 1, "postgresql")).toBeNull());
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd web && npx vitest run splitStatements`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement the splitter**

`web/src/sql/splitStatements.ts`:

```ts
export type DialectId =
  | "postgresql" | "mysql" | "sqlserver" | "sqlite" | "oracle" | "duckdb" | "clickhouse";

export interface SqlStatement { text: string; start: number; end: number }

const GO_DIALECTS: DialectId[] = ["sqlserver"];

/// Mirrors the server's StatementSplitter. A character scanner: it only tracks strings, comments,
/// quoted identifiers and dollar-quoted bodies, which is all that semicolon detection needs.
export function splitStatements(sql: string, dialect: DialectId): SqlStatement[] {
  const out: SqlStatement[] = [];
  const usesGo = GO_DIALECTS.includes(dialect);
  let start = 0;
  let i = 0;

  const flush = (end: number) => {
    const text = sql.slice(start, end);
    if (text.trim().length > 0) out.push({ text, start, end });
    start = end + 1;
  };

  while (i < sql.length) {
    const c = sql[i];

    if (c === "-" && sql[i + 1] === "-") {
      while (i < sql.length && sql[i] !== "\n") i++;
      continue;
    }
    if (c === "/" && sql[i + 1] === "*") {
      const close = sql.indexOf("*/", i + 2);
      i = close === -1 ? sql.length : close + 2;
      continue;
    }
    if (c === "'" || c === '"' || c === "`" || c === "[") {
      const close = c === "[" ? "]" : c;
      i++;
      while (i < sql.length) {
        if (sql[i] === close && sql[i + 1] === close) { i += 2; continue; }
        if (sql[i] === close) { i++; break; }
        i++;
      }
      continue;
    }
    if (c === "$") {
      const tagEnd = sql.indexOf("$", i + 1);
      if (tagEnd > i) {
        const tag = sql.slice(i, tagEnd + 1);
        const end = sql.indexOf(tag, tagEnd + 1);
        if (end > 0) { i = end + tag.length; continue; }
      }
    }
    if (usesGo && (c === "g" || c === "G") && isGoLine(sql, i)) {
      flush(i);
      i = sql.indexOf("\n", i);
      if (i === -1) { i = sql.length; break; }
      start = i + 1;
      i++;
      continue;
    }
    if (c === ";") { flush(i); i++; continue; }
    i++;
  }

  flush(sql.length);
  return out;
}

function isGoLine(sql: string, i: number): boolean {
  const lineStart = sql.lastIndexOf("\n", Math.max(i - 1, 0)) + 1;
  if (sql.slice(lineStart, i).trim() !== "") return false;
  if (sql.slice(i, i + 2).toUpperCase() !== "GO") return false;
  const rest = sql.slice(i + 2);
  return /^[ \t\r]*(\n|$)/.test(rest);
}

/// The statement the cursor sits in — what F5 runs when nothing is selected.
export function statementAt(sql: string, offset: number, dialect: DialectId): SqlStatement | null {
  const statements = splitStatements(sql, dialect);
  if (statements.length === 0) return null;

  for (const s of statements) if (offset >= s.start && offset <= s.end) return s;

  // The cursor is past the last terminator: run the last statement.
  return statements[statements.length - 1];
}
```

- [ ] **Step 4: Run the tests**

Run: `cd web && npx vitest run splitStatements`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: client-side statement splitter"
```

---

### Task 2: NDJSON streaming client

**Files:**
- Create: `web/src/query/runQuery.ts`
- Create: `web/src/query/runQuery.test.ts`

**Interfaces:**
- Consumes: `POST /api/query/execute`, `POST /api/query/{runId}/cancel`.
- Produces:
  - `type QueryChunk` — the discriminated union of the wire shapes from spec 5.3.
  - `runQuery(request, onChunk): { runId: Promise<string>; done: Promise<void>; cancel(): Promise<void> }`
  - `interface QueryRequest { connectionId: string; sql: string; maxRows?: number; timeoutSeconds?: number; schema?: string }`

- [ ] **Step 1: Write the failing test**

`web/src/query/runQuery.test.ts`:

```ts
import { describe, expect, it, vi } from "vitest";
import { runQuery, type QueryChunk } from "./runQuery";

function streamOf(lines: string[]): Response {
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      const encoder = new TextEncoder();
      // Split one chunk mid-line to prove the reader buffers partial lines.
      const text = lines.join("\n") + "\n";
      controller.enqueue(encoder.encode(text.slice(0, 12)));
      controller.enqueue(encoder.encode(text.slice(12)));
      controller.close();
    },
  });
  return new Response(body, { status: 200, headers: { "X-Run-Id": "run1" } });
}

describe("runQuery", () => {
  it("delivers each chunk in order, even across split reads", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => streamOf([
      '{"type":"columns","statement":0,"columns":[{"name":"id"}]}',
      '{"type":"rows","statement":0,"rows":[[1],[2]]}',
      '{"type":"end","statement":0,"rowsAffected":0,"elapsedMs":3,"truncated":false}',
    ])));

    const seen: QueryChunk[] = [];
    const run = runQuery({ connectionId: "c1", sql: "SELECT id FROM t" }, c => seen.push(c));
    await run.done;

    expect(seen.map(c => c.type)).toEqual(["columns", "rows", "end"]);
    expect((seen[1] as { rows: unknown[][] }).rows).toHaveLength(2);
  });

  it("exposes the run id from the response header", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => streamOf(['{"type":"end","statement":0}'])));
    const run = runQuery({ connectionId: "c1", sql: "SELECT 1" }, () => {});
    await run.done;
    expect(await run.runId).toBe("run1");
  });

  it("ignores a malformed line instead of failing the whole run", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => streamOf([
      "not json",
      '{"type":"end","statement":0}',
    ])));

    const seen: QueryChunk[] = [];
    await runQuery({ connectionId: "c1", sql: "SELECT 1" }, c => seen.push(c)).done;
    expect(seen.map(c => c.type)).toEqual(["end"]);
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd web && npx vitest run runQuery`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement the client**

`web/src/query/runQuery.ts`:

```ts
export interface QueryColumn { name: string; dataType: string; nullable: boolean }

export type QueryChunk =
  | { type: "columns"; statement: number; columns: QueryColumn[] }
  | { type: "rows"; statement: number; rows: unknown[][] }
  | { type: "progress"; statement: number; rowsRead: number; elapsedMs: number }
  | { type: "message"; statement: number; severity: string; text: string }
  | { type: "end"; statement: number; rowsAffected: number; elapsedMs: number; truncated: boolean }
  | { type: "error"; statement: number; text: string; code: string | null; line: number | null; column: number | null }
  | { type: "cancelled" };

export interface QueryRequest {
  connectionId: string; sql: string;
  maxRows?: number; timeoutSeconds?: number; schema?: string;
}

export interface QueryRun {
  runId: Promise<string | null>;
  done: Promise<void>;
  cancel: () => Promise<void>;
}

/// Streams NDJSON from /api/query/execute, calling onChunk as each line arrives so the grid can
/// render while the query is still running.
export function runQuery(request: QueryRequest, onChunk: (chunk: QueryChunk) => void): QueryRun {
  let resolveRunId: (id: string | null) => void = () => {};
  const runId = new Promise<string | null>(resolve => { resolveRunId = resolve; });
  let currentRunId: string | null = null;

  const done = (async () => {
    const response = await fetch("/api/query/execute", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(request),
    });

    currentRunId = response.headers.get("X-Run-Id");
    resolveRunId(currentRunId);

    if (!response.ok) {
      const text = await response.text();
      let message = text;
      try { const j = JSON.parse(text); if (j?.message) message = j.message; } catch { /* not JSON */ }
      onChunk({ type: "error", statement: 0, text: message, code: null, line: null, column: null });
      return;
    }

    const reader = response.body!.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    for (;;) {
      const { done: finished, value } = await reader.read();
      if (finished) break;
      buffer += decoder.decode(value, { stream: true });

      let newline: number;
      while ((newline = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, newline).trim();
        buffer = buffer.slice(newline + 1);
        if (line) emit(line, onChunk);
      }
    }
    if (buffer.trim()) emit(buffer.trim(), onChunk);
  })();

  return {
    runId,
    done,
    cancel: async () => {
      const id = currentRunId ?? (await runId);
      if (id) await fetch(`/api/query/${id}/cancel`, { method: "POST" });
    },
  };
}

function emit(line: string, onChunk: (chunk: QueryChunk) => void) {
  try { onChunk(JSON.parse(line) as QueryChunk); }
  catch { /* a malformed line must not kill a run that is otherwise fine */ }
}
```

- [ ] **Step 4: Run the tests**

Run: `cd web && npx vitest run runQuery`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: NDJSON query streaming client"
```

---

### Task 3: Result store

**Files:**
- Create: `web/src/query/resultStore.ts`
- Create: `web/src/query/resultStore.test.ts`

**Interfaces:**
- Consumes: `QueryChunk` from Task 2.
- Produces: `createResultState()` and `applyChunk(state, chunk): ResultState` where
  `interface ResultState { statements: StatementResult[]; messages: Message[]; cancelled: boolean }`
  and `interface StatementResult { index: number; columns: QueryColumn[]; rows: unknown[][]; rowsAffected: number | null; elapsedMs: number | null; truncated: boolean; error: QueryError | null; running: boolean }`.

- [ ] **Step 1: Write the failing test**

`web/src/query/resultStore.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { applyChunk, createResultState } from "./resultStore";

describe("resultStore", () => {
  it("accumulates rows across chunks", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "columns", statement: 0, columns: [{ name: "id", dataType: "int", nullable: false }] });
    state = applyChunk(state, { type: "rows", statement: 0, rows: [[1]] });
    state = applyChunk(state, { type: "rows", statement: 0, rows: [[2]] });

    expect(state.statements[0].rows).toEqual([[1], [2]]);
    expect(state.statements[0].running).toBe(true);
  });

  it("marks a statement finished and records its timing", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "columns", statement: 0, columns: [] });
    state = applyChunk(state, { type: "end", statement: 0, rowsAffected: 3, elapsedMs: 42, truncated: true });

    expect(state.statements[0].running).toBe(false);
    expect(state.statements[0].elapsedMs).toBe(42);
    expect(state.statements[0].truncated).toBe(true);
  });

  it("keeps statements separate", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "rows", statement: 0, rows: [[1]] });
    state = applyChunk(state, { type: "rows", statement: 1, rows: [[9]] });

    expect(state.statements).toHaveLength(2);
    expect(state.statements[1].rows).toEqual([[9]]);
  });

  it("stores an error on its statement", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "error", statement: 0, text: "boom", code: "42601", line: 2, column: 5 });

    expect(state.statements[0].error).toEqual({ text: "boom", code: "42601", line: 2, column: 5 });
    expect(state.statements[0].running).toBe(false);
  });

  it("records cancellation", () => {
    const state = applyChunk(createResultState(), { type: "cancelled" });
    expect(state.cancelled).toBe(true);
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd web && npx vitest run resultStore`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement the store**

`web/src/query/resultStore.ts`:

```ts
import type { QueryChunk, QueryColumn } from "./runQuery";

export interface QueryError { text: string; code: string | null; line: number | null; column: number | null }

export interface StatementResult {
  index: number;
  columns: QueryColumn[];
  rows: unknown[][];
  rowsAffected: number | null;
  elapsedMs: number | null;
  rowsRead: number;
  truncated: boolean;
  error: QueryError | null;
  running: boolean;
}

export interface ResultMessage { statement: number; severity: string; text: string }

export interface ResultState {
  statements: StatementResult[];
  messages: ResultMessage[];
  cancelled: boolean;
}

export const createResultState = (): ResultState => ({ statements: [], messages: [], cancelled: false });

const empty = (index: number): StatementResult => ({
  index, columns: [], rows: [], rowsAffected: null, elapsedMs: null,
  rowsRead: 0, truncated: false, error: null, running: true,
});

/// Pure reducer: the panel keeps one ResultState in React state and replaces it per chunk. Rows are
/// pushed into the existing array (not copied) because a 100k-row copy per chunk would crawl.
export function applyChunk(state: ResultState, chunk: QueryChunk): ResultState {
  if (chunk.type === "cancelled") return { ...state, cancelled: true };

  const statements = state.statements.slice();
  while (statements.length <= chunk.statement) statements.push(empty(statements.length));
  const target = { ...statements[chunk.statement] };

  switch (chunk.type) {
    case "columns":
      target.columns = chunk.columns;
      break;
    case "rows":
      target.rows = target.rows.concat(chunk.rows);
      target.rowsRead = target.rows.length;
      break;
    case "progress":
      target.rowsRead = chunk.rowsRead;
      target.elapsedMs = chunk.elapsedMs;
      break;
    case "message":
      statements[chunk.statement] = target;
      return {
        ...state,
        statements,
        messages: state.messages.concat({ statement: chunk.statement, severity: chunk.severity, text: chunk.text }),
      };
    case "end":
      target.rowsAffected = chunk.rowsAffected;
      target.elapsedMs = chunk.elapsedMs;
      target.truncated = chunk.truncated;
      target.running = false;
      break;
    case "error":
      target.error = { text: chunk.text, code: chunk.code, line: chunk.line, column: chunk.column };
      target.running = false;
      break;
  }

  statements[chunk.statement] = target;
  return { ...state, statements };
}
```

- [ ] **Step 4: Run the tests**

Run: `cd web && npx vitest run resultStore`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: result state reducer"
```

---

### Task 4: Virtualised result grid

**Files:**
- Create: `web/src/grid/ResultGrid.tsx`
- Create: `web/src/grid/CellValue.tsx`
- Create: `web/src/grid/aggregate.ts`
- Create: `web/src/grid/aggregate.test.ts`
- Modify: `web/package.json` (add `@tanstack/react-table`, `@tanstack/react-virtual`)

**Interfaces:**
- Consumes: `StatementResult` from Task 3.
- Produces:
  - `summarizeSelection(values: unknown[]): { count: number; numeric: number; sum: number | null; avg: number | null; min: number | null; max: number | null }`
  - `<ResultGrid result={StatementResult} onSelectionChange={(cells) => void} />`
  - `<CellValue value={unknown} />` — the NULL-versus-empty-string rendering used everywhere.

- [ ] **Step 1: Add the dependencies**

```bash
cd web && npm install @tanstack/react-table @tanstack/react-virtual
```

- [ ] **Step 2: Write the failing test**

`web/src/grid/aggregate.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { summarizeSelection } from "./aggregate";

describe("summarizeSelection", () => {
  it("counts every selected cell", () =>
    expect(summarizeSelection([1, "x", null]).count).toBe(3));

  it("aggregates only the numeric cells", () => {
    const s = summarizeSelection([1, 2, "x", null, 3]);
    expect(s.numeric).toBe(3);
    expect(s.sum).toBe(6);
    expect(s.avg).toBe(2);
    expect(s.min).toBe(1);
    expect(s.max).toBe(3);
  });

  it("treats numeric strings as numbers", () =>
    expect(summarizeSelection(["1.5", "2.5"]).sum).toBe(4));

  it("returns null aggregates when nothing is numeric", () => {
    const s = summarizeSelection(["a", null]);
    expect(s.sum).toBeNull();
    expect(s.avg).toBeNull();
  });
});
```

- [ ] **Step 3: Run it to verify it fails**

Run: `cd web && npx vitest run aggregate`
Expected: FAIL — module not found.

- [ ] **Step 4: Implement the aggregate helper**

`web/src/grid/aggregate.ts`:

```ts
export interface SelectionSummary {
  count: number; numeric: number;
  sum: number | null; avg: number | null; min: number | null; max: number | null;
}

/// The status-bar summary of a grid selection. Numeric strings count as numbers because most
/// drivers return DECIMAL as a string to avoid precision loss.
export function summarizeSelection(values: unknown[]): SelectionSummary {
  const numbers: number[] = [];
  for (const value of values) {
    if (value === null || value === undefined || value === "") continue;
    const n = typeof value === "number" ? value : Number(value);
    if (Number.isFinite(n)) numbers.push(n);
  }

  if (numbers.length === 0)
    return { count: values.length, numeric: 0, sum: null, avg: null, min: null, max: null };

  const sum = numbers.reduce((a, b) => a + b, 0);
  return {
    count: values.length,
    numeric: numbers.length,
    sum,
    avg: sum / numbers.length,
    min: Math.min(...numbers),
    max: Math.max(...numbers),
  };
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `cd web && npx vitest run aggregate`
Expected: PASS, 4 tests.

- [ ] **Step 6: Write the cell renderer**

`web/src/grid/CellValue.tsx`:

```tsx
import { Text } from "@mantine/core";

/// NULL must never look like an empty string — a wrong read here costs the user real time.
export function CellValue({ value }: { value: unknown }) {
  if (value === null || value === undefined)
    return <Text component="span" size="xs" c="dimmed" fs="italic">NULL</Text>;
  if (value === "")
    return <Text component="span" size="xs" c="dimmed">&#x2205;</Text>;
  if (typeof value === "boolean")
    return <Text component="span" size="xs">{value ? "true" : "false"}</Text>;
  if (typeof value === "object")
    return <Text component="span" size="xs" ff="monospace">{JSON.stringify(value)}</Text>;
  return <Text component="span" size="xs">{String(value)}</Text>;
}
```

- [ ] **Step 7: Write the grid**

`web/src/grid/ResultGrid.tsx`:

```tsx
import { useMemo, useRef, useState } from "react";
import { Group, Menu, Text, TextInput } from "@mantine/core";
import { IconFilter, IconSortAscending, IconSortDescending } from "@tabler/icons-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { StatementResult } from "../query/resultStore";
import { CellValue } from "./CellValue";
import { summarizeSelection } from "./aggregate";

const ROW_HEIGHT = 24;

export function ResultGrid({ result }: { result: StatementResult }) {
  const parentRef = useRef<HTMLDivElement>(null);
  const [sort, setSort] = useState<{ index: number; desc: boolean } | null>(null);
  const [filters, setFilters] = useState<Record<number, string>>({});
  const [search, setSearch] = useState("");
  const [hidden, setHidden] = useState<Set<number>>(new Set());
  const [selected, setSelected] = useState<{ row: number; col: number }[]>([]);

  const visibleColumns = result.columns
    .map((c, index) => ({ ...c, index }))
    .filter(c => !hidden.has(c.index));

  // Sorting and filtering happen client-side over the rows already fetched. Anything beyond the
  // fetch cap needs a server round trip, which the toolbar's "load more" triggers.
  const rows = useMemo(() => {
    let out = result.rows;

    const active = Object.entries(filters).filter(([, v]) => v.trim() !== "");
    if (active.length > 0)
      out = out.filter(row => active.every(([i, v]) =>
        String(row[Number(i)] ?? "").toLowerCase().includes(v.toLowerCase())));

    if (search.trim() !== "")
      out = out.filter(row => row.some(cell =>
        String(cell ?? "").toLowerCase().includes(search.toLowerCase())));

    if (sort) {
      const { index, desc } = sort;
      out = out.slice().sort((a, b) => {
        const x = a[index];
        const y = b[index];
        if (x === null || x === undefined) return desc ? 1 : -1;
        if (y === null || y === undefined) return desc ? -1 : 1;
        const nx = Number(x);
        const ny = Number(y);
        const cmp = Number.isFinite(nx) && Number.isFinite(ny)
          ? nx - ny
          : String(x).localeCompare(String(y));
        return desc ? -cmp : cmp;
      });
    }
    return out;
  }, [result.rows, filters, search, sort]);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 20,
  });

  const summary = summarizeSelection(selected.map(s => rows[s.row]?.[s.col]));

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <Group gap={6} p={4} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="Search in result"
          value={search} onChange={e => setSearch(e.currentTarget.value)} />
        <Text size="xs" c="dimmed">
          {rows.length} of {result.rows.length} rows
          {result.truncated && " (capped)"}
          {result.elapsedMs !== null && ` · ${result.elapsedMs} ms`}
        </Text>
      </Group>

      <div ref={parentRef} style={{ flex: 1, overflow: "auto" }}>
        <table style={{ borderCollapse: "collapse", width: "max-content", minWidth: "100%" }}>
          <thead style={{ position: "sticky", top: 0, zIndex: 1, background: "var(--mantine-color-default)" }}>
            <tr>
              {visibleColumns.map(c => (
                <th key={c.index} style={{ textAlign: "left", padding: "2px 8px", whiteSpace: "nowrap",
                                           borderBottom: "1px solid var(--mantine-color-default-border)" }}>
                  <Menu withinPortal>
                    <Menu.Target>
                      <Group gap={2} style={{ cursor: "pointer" }} wrap="nowrap">
                        <Text size="xs" fw={600}>{c.name}</Text>
                        {sort?.index === c.index && (sort.desc
                          ? <IconSortDescending size={12} /> : <IconSortAscending size={12} />)}
                        {filters[c.index] && <IconFilter size={12} />}
                      </Group>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item onClick={() => setSort({ index: c.index, desc: false })}>Sort ascending</Menu.Item>
                      <Menu.Item onClick={() => setSort({ index: c.index, desc: true })}>Sort descending</Menu.Item>
                      <Menu.Item onClick={() => setSort(null)}>Clear sort</Menu.Item>
                      <Menu.Divider />
                      <Menu.Item closeMenuOnClick={false}>
                        <TextInput size="xs" placeholder="Filter" value={filters[c.index] ?? ""}
                          onChange={e => setFilters(f => ({ ...f, [c.index]: e.currentTarget.value }))} />
                      </Menu.Item>
                      <Menu.Divider />
                      <Menu.Item onClick={() => setHidden(h => new Set(h).add(c.index))}>Hide column</Menu.Item>
                      <Menu.Item onClick={() => setHidden(new Set())}>Show all columns</Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                  <Text size="10px" c="dimmed">{c.dataType}</Text>
                </th>
              ))}
            </tr>
          </thead>
          <tbody style={{ position: "relative", height: virtualizer.getTotalSize() }}>
            {virtualizer.getVirtualItems().map(item => (
              <tr key={item.key} style={{ position: "absolute", top: 0, left: 0, width: "100%",
                                          height: ROW_HEIGHT, transform: `translateY(${item.start}px)` }}>
                {visibleColumns.map(c => {
                  const isSelected = selected.some(s => s.row === item.index && s.col === c.index);
                  return (
                    <td key={c.index}
                      onMouseDown={e => setSelected(prev => e.ctrlKey || e.metaKey
                        ? [...prev, { row: item.index, col: c.index }]
                        : [{ row: item.index, col: c.index }])}
                      style={{
                        padding: "2px 8px", whiteSpace: "nowrap", cursor: "cell",
                        borderBottom: "1px solid var(--mantine-color-default-border)",
                        background: isSelected ? "var(--mantine-primary-color-light)" : undefined,
                      }}>
                      <CellValue value={rows[item.index]?.[c.index]} />
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {selected.length > 0 && (
        <Group gap={12} px={8} py={2} style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>
          <Text size="xs" c="dimmed">Selected: {summary.count}</Text>
          {summary.sum !== null && <Text size="xs" c="dimmed">Sum: {summary.sum}</Text>}
          {summary.avg !== null && <Text size="xs" c="dimmed">Avg: {summary.avg.toFixed(4)}</Text>}
          {summary.min !== null && <Text size="xs" c="dimmed">Min: {summary.min}</Text>}
          {summary.max !== null && <Text size="xs" c="dimmed">Max: {summary.max}</Text>}
        </Group>
      )}
    </div>
  );
}
```

- [ ] **Step 8: Add the cell value viewer and the form view**

Create `web/src/grid/CellViewerModal.tsx` with a Mantine `Modal` that shows the active cell in four
tabs — Text, JSON (pretty-printed when `JSON.parse` succeeds), Hex (for values starting with `0x`),
Image (an `<img>` when the hex decodes to a PNG/JPEG magic number) — and a Download button that
turns the value into a Blob. Wire it to a double-click on a grid cell.

Create `web/src/grid/RowFormView.tsx` showing one row as a vertical label/value list, toggled from
the result toolbar. It reuses `<CellValue>` so NULL rendering stays consistent.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: virtualised result grid with sorting, filtering and selection aggregates"
```

---

### Task 5: Monaco query panel

**Files:**
- Create: `web/src/editor/QueryEditor.tsx`
- Create: `web/src/editor/monacoSetup.ts`
- Create: `web/src/editor/useActiveStatement.ts`
- Modify: `web/vite.config.ts` (monaco worker configuration)

**Interfaces:**
- Consumes: `splitStatements`, `statementAt` from Task 1.
- Produces:
  - `configureMonaco()` — registers the workers and the SQL language once per page.
  - `<QueryEditor value onChange onRun dialect />` where `onRun(sql: string)` receives the selection
    if there is one, otherwise the statement under the cursor.

- [ ] **Step 1: Configure Monaco workers**

`web/src/editor/monacoSetup.ts`:

```ts
import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";

let configured = false;

/// Monaco needs its worker wired up explicitly under Vite. Only the base editor worker is needed:
/// SQL has no dedicated language service worker.
export function configureMonaco() {
  if (configured) return;
  configured = true;
  self.MonacoEnvironment = { getWorker: () => new editorWorker() };
  monaco.languages.register({ id: "sql" });
}
```

- [ ] **Step 2: Write the active-statement hook**

`web/src/editor/useActiveStatement.ts`:

```ts
import { useEffect } from "react";
import * as monaco from "monaco-editor";
import { statementAt, type DialectId } from "../sql/splitStatements";

/// Highlights the statement the cursor sits in, so F5 never runs something the user did not expect.
export function useActiveStatement(
  editor: monaco.editor.IStandaloneCodeEditor | null,
  dialect: DialectId,
) {
  useEffect(() => {
    if (!editor) return;
    let collection = editor.createDecorationsCollection([]);

    const update = () => {
      const model = editor.getModel();
      const position = editor.getPosition();
      if (!model || !position) return;

      const selection = editor.getSelection();
      if (selection && !selection.isEmpty()) { collection.set([]); return; }

      const statement = statementAt(model.getValue(), model.getOffsetAt(position), dialect);
      if (!statement) { collection.set([]); return; }

      const start = model.getPositionAt(statement.start);
      const end = model.getPositionAt(statement.end);
      collection.set([{
        range: new monaco.Range(start.lineNumber, 1, end.lineNumber, model.getLineMaxColumn(end.lineNumber)),
        options: { isWholeLine: true, className: "wds-active-statement" },
      }]);
    };

    const a = editor.onDidChangeCursorPosition(update);
    const b = editor.onDidChangeModelContent(update);
    update();
    return () => { a.dispose(); b.dispose(); collection.clear(); };
  }, [editor, dialect]);
}
```

Add to `web/src/editor/dockview-mantine.css`:

```css
/* The statement F5 would run, tinted just enough to be visible in every theme. */
.wds-active-statement {
  background-color: color-mix(in srgb, var(--mantine-primary-color-filled) 10%, transparent);
}
```

- [ ] **Step 3: Write the editor component**

`web/src/editor/QueryEditor.tsx`:

```tsx
import { useEffect, useRef, useState } from "react";
import * as monaco from "monaco-editor";
import { useAppTheme } from "../ThemeProvider";
import { configureMonaco } from "./monacoSetup";
import { useActiveStatement } from "./useActiveStatement";
import { statementAt, type DialectId } from "../sql/splitStatements";

export function QueryEditor({ value, dialect, onChange, onRun, onRunAll }: {
  value: string;
  dialect: DialectId;
  onChange: (sql: string) => void;
  onRun: (sql: string) => void;
  onRunAll: (sql: string) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const [editor, setEditor] = useState<monaco.editor.IStandaloneCodeEditor | null>(null);
  const { current } = useAppTheme();

  useEffect(() => {
    if (!host.current) return;
    configureMonaco();
    const instance = monaco.editor.create(host.current, {
      value, language: "sql", theme: current.monaco,
      automaticLayout: true, minimap: { enabled: false },
      fontSize: 13, scrollBeyondLastLine: false, renderWhitespace: "selection",
    });
    setEditor(instance);
    const sub = instance.onDidChangeModelContent(() => onChange(instance.getValue()));
    return () => { sub.dispose(); instance.dispose(); };
    // Created once: value changes flow through the model, not through recreation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Theme switches must restyle the editor without a reload.
  useEffect(() => { monaco.editor.setTheme(current.monaco); }, [current.monaco]);

  useActiveStatement(editor, dialect);

  // Keybindings are re-registered whenever the callbacks change so they never close over stale state.
  useEffect(() => {
    if (!editor) return;
    const run = () => {
      const model = editor.getModel();
      const selection = editor.getSelection();
      if (!model) return;

      if (selection && !selection.isEmpty()) { onRun(model.getValueInRange(selection)); return; }
      const position = editor.getPosition();
      if (!position) return;
      const statement = statementAt(model.getValue(), model.getOffsetAt(position), dialect);
      if (statement) onRun(statement.text);
    };

    const one = editor.addAction({
      id: "wds.run", label: "Run selection or statement",
      keybindings: [monaco.KeyCode.F5, monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
      run,
    });
    const all = editor.addAction({
      id: "wds.runAll", label: "Run whole script",
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.Enter],
      run: () => onRunAll(editor.getValue()),
    });
    return () => { one.dispose(); all.dispose(); };
  }, [editor, dialect, onRun, onRunAll]);

  return <div ref={host} style={{ height: "100%", width: "100%" }} />;
}
```

- [ ] **Step 4: Verify by hand**

Mount `<QueryEditor>` temporarily in `DockShell`, type two statements, and confirm: the statement
under the cursor is tinted, F5 runs only that one, selecting text and pressing F5 runs the
selection, and switching the theme restyles the editor immediately.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Monaco query editor with selection execution and active statement highlight"
```

---

### Task 6: Schema-aware completion, formatting, hover and error marking

**Files:**
- Create: `web/src/editor/completion.ts`
- Create: `web/src/editor/completion.test.ts`
- Create: `web/src/editor/schemaCache.ts`
- Create: `web/src/editor/formatSql.ts`
- Modify: `web/src/editor/QueryEditor.tsx`
- Modify: `web/package.json` (add `sql-formatter`)

**Interfaces:**
- Consumes: `listSchema`, `describeObject` from P1 Task 9.
- Produces:
  - `SchemaCache` with `tables(connectionId): Promise<TableRef[]>`, `columns(connectionId, table): Promise<string[]>`, `invalidate(connectionId)`.
  - `collectAliases(sql: string): Map<string, string>` — alias to table name, used by column completion.
  - `completionItems(context): CompletionCandidate[]`
  - `formatSql(sql, dialect): string`
  - `markErrors(editor, error)` — turns a `QueryError` into a Monaco marker.

- [ ] **Step 1: Write the failing test**

`web/src/editor/completion.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { collectAliases, completionContext } from "./completion";

describe("collectAliases", () => {
  it("finds an explicit alias", () =>
    expect(collectAliases("SELECT * FROM users u").get("u")).toBe("users"));

  it("finds an AS alias", () =>
    expect(collectAliases("SELECT * FROM users AS u").get("u")).toBe("users"));

  it("finds join aliases too", () => {
    const aliases = collectAliases("SELECT * FROM users u JOIN orders o ON o.user_id = u.id");
    expect(aliases.get("o")).toBe("orders");
  });

  it("maps a schema-qualified table to its bare name", () =>
    expect(collectAliases("SELECT * FROM public.users u").get("u")).toBe("users"));
});

describe("completionContext", () => {
  it("asks for columns of the aliased table after a dot", () => {
    const context = completionContext("SELECT u. FROM users u", 9);
    expect(context).toEqual({ kind: "columns", table: "users" });
  });

  it("asks for tables right after FROM", () =>
    expect(completionContext("SELECT * FROM ", 14).kind).toBe("tables"));

  it("falls back to everything elsewhere", () =>
    expect(completionContext("SELECT ", 7).kind).toBe("any"));
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd web && npx vitest run completion`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement the completion logic**

`web/src/editor/completion.ts`:

```ts
export type CompletionContext =
  | { kind: "columns"; table: string }
  | { kind: "tables" }
  | { kind: "any" };

const FROM_JOIN = /\b(?:from|join|update|into)\s+(?:([A-Za-z_][\w$]*)\.)?([A-Za-z_][\w$]*)(?:\s+(?:as\s+)?([A-Za-z_][\w$]*))?/gi;

/// Maps each alias in the statement to the bare table name it refers to. Also maps a table's own
/// name to itself so `users.` completes without an alias.
export function collectAliases(sql: string): Map<string, string> {
  const aliases = new Map<string, string>();
  for (const match of sql.matchAll(FROM_JOIN)) {
    const table = match[2];
    const alias = match[3];
    if (!table) continue;
    aliases.set(table.toLowerCase(), table);
    if (alias && !isKeyword(alias)) aliases.set(alias.toLowerCase(), table);
  }
  return aliases;
}

const AFTER_KEYWORDS = /\b(from|join|update|into|table)\s+[\w."]*$/i;

export function completionContext(sql: string, offset: number): CompletionContext {
  const before = sql.slice(0, offset);

  const dotted = before.match(/([A-Za-z_][\w$]*)\.\s*[\w$]*$/);
  if (dotted) {
    const table = collectAliases(sql).get(dotted[1].toLowerCase());
    if (table) return { kind: "columns", table };
  }

  if (AFTER_KEYWORDS.test(before)) return { kind: "tables" };
  return { kind: "any" };
}

const KEYWORDS = new Set([
  "on", "where", "group", "order", "having", "limit", "offset", "set", "values",
  "inner", "left", "right", "full", "outer", "cross", "join", "as", "using",
]);
const isKeyword = (word: string) => KEYWORDS.has(word.toLowerCase());

export const SQL_KEYWORDS = [
  "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
  "INSERT INTO", "UPDATE", "DELETE FROM", "VALUES", "SET", "JOIN", "LEFT JOIN",
  "RIGHT JOIN", "INNER JOIN", "FULL JOIN", "ON", "AS", "DISTINCT", "COUNT", "SUM",
  "AVG", "MIN", "MAX", "CASE", "WHEN", "THEN", "ELSE", "END", "AND", "OR", "NOT",
  "NULL", "IS NULL", "IS NOT NULL", "IN", "EXISTS", "BETWEEN", "LIKE", "UNION",
  "UNION ALL", "WITH", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE INDEX",
];
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd web && npx vitest run completion`
Expected: PASS, 7 tests.

- [ ] **Step 5: Implement the schema cache**

`web/src/editor/schemaCache.ts`:

```ts
import { describeObject, listSchema, type SchemaNodeDto } from "../api";

export interface TableRef { name: string; ref: string; schema: string }

/// Completion must not re-walk the schema on every keystroke; the tree is fetched once per
/// connection and dropped when the explorer refreshes.
export class SchemaCache {
  private tablesByConnection = new Map<string, Promise<TableRef[]>>();
  private columnsByRef = new Map<string, Promise<string[]>>();

  invalidate(connectionId: string) {
    this.tablesByConnection.delete(connectionId);
    for (const key of [...this.columnsByRef.keys()])
      if (key.startsWith(`${connectionId}:`)) this.columnsByRef.delete(key);
  }

  tables(connectionId: string): Promise<TableRef[]> {
    let cached = this.tablesByConnection.get(connectionId);
    if (!cached) {
      cached = this.loadTables(connectionId);
      this.tablesByConnection.set(connectionId, cached);
    }
    return cached;
  }

  async columns(connectionId: string, tableName: string): Promise<string[]> {
    const table = (await this.tables(connectionId))
      .find(t => t.name.toLowerCase() === tableName.toLowerCase());
    if (!table) return [];

    const key = `${connectionId}:${table.ref}`;
    let cached = this.columnsByRef.get(key);
    if (!cached) {
      cached = describeObject(connectionId, table.ref).then(d => d.columns.map(c => c.name));
      this.columnsByRef.set(key, cached);
    }
    return cached;
  }

  private async loadTables(connectionId: string): Promise<TableRef[]> {
    const out: TableRef[] = [];
    const roots = await listSchema(connectionId);

    const walk = async (node: SchemaNodeDto, schema: string) => {
      if (node.kind === "Table" || node.kind === "View") {
        out.push({ name: node.label, ref: node.ref, schema });
        return;
      }
      if (!node.hasChildren) return;
      const children = await listSchema(connectionId, node.ref);
      await Promise.all(children.map(child =>
        walk(child, node.kind === "Schema" ? node.label : schema)));
    };

    await Promise.all(roots.map(node => walk(node, node.label)));
    return out;
  }
}

export const schemaCache = new SchemaCache();
```

- [ ] **Step 6: Register the Monaco providers**

Add to `QueryEditor.tsx` an effect that registers, and disposes on unmount, three providers scoped
to the current connection:

```tsx
useEffect(() => {
  const completion = monaco.languages.registerCompletionItemProvider("sql", {
    triggerCharacters: [".", " "],
    provideCompletionItems: async (model, position) => {
      const offset = model.getOffsetAt(position);
      const word = model.getWordUntilPosition(position);
      const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
      const context = completionContext(model.getValue(), offset);

      const item = (label: string, kind: monaco.languages.CompletionItemKind, detail?: string) =>
        ({ label, kind, insertText: label, range, detail });

      if (context.kind === "columns") {
        const columns = await schemaCache.columns(connectionId, context.table);
        return { suggestions: columns.map(c => item(c, monaco.languages.CompletionItemKind.Field, context.table)) };
      }

      const tables = await schemaCache.tables(connectionId);
      const tableItems = tables.map(t => item(t.name, monaco.languages.CompletionItemKind.Struct, t.schema));
      if (context.kind === "tables") return { suggestions: tableItems };

      return {
        suggestions: [
          ...tableItems,
          ...SQL_KEYWORDS.map(k => item(k, monaco.languages.CompletionItemKind.Keyword)),
        ],
      };
    },
  });

  const hover = monaco.languages.registerHoverProvider("sql", {
    provideHover: async (model, position) => {
      const word = model.getWordAtPosition(position);
      if (!word) return null;
      const columns = await schemaCache.columns(connectionId, word.word);
      if (columns.length === 0) return null;
      return {
        contents: [
          { value: `**${word.word}**` },
          { value: columns.map(c => `- ${c}`).join("\n") },
        ],
      };
    },
  });

  const definition = monaco.languages.registerDefinitionProvider("sql", {
    provideDefinition: async (model, position) => {
      const word = model.getWordAtPosition(position);
      if (!word) return null;
      const tables = await schemaCache.tables(connectionId);
      const table = tables.find(t => t.name.toLowerCase() === word.word.toLowerCase());
      // Opening the object in the explorer is more useful than jumping inside the text buffer.
      if (table) onOpenObject(table.ref);
      return null;
    },
  });

  return () => { completion.dispose(); hover.dispose(); definition.dispose(); };
}, [connectionId, onOpenObject]);
```

- [ ] **Step 7: Add the formatter and error marking**

```bash
cd web && npm install sql-formatter
```

`web/src/editor/formatSql.ts`:

```ts
import { format } from "sql-formatter";
import type { DialectId } from "../sql/splitStatements";

const LANGUAGES: Record<DialectId, Parameters<typeof format>[1]["language"]> = {
  postgresql: "postgresql", mysql: "mysql", sqlserver: "tsql", sqlite: "sqlite",
  oracle: "plsql", duckdb: "postgresql", clickhouse: "sql",
};

export const formatSql = (sql: string, dialect: DialectId): string =>
  format(sql, { language: LANGUAGES[dialect] ?? "sql", keywordCase: "upper" });
```

Add a `markErrors` helper in `QueryEditor.tsx` that converts a `QueryError` into a Monaco marker:

```tsx
export function markErrors(editor: monaco.editor.IStandaloneCodeEditor, error: QueryError | null) {
  const model = editor.getModel();
  if (!model) return;
  if (!error) { monaco.editor.setModelMarkers(model, "wds", []); return; }

  const line = error.line ?? 1;
  const column = error.column ?? 1;
  monaco.editor.setModelMarkers(model, "wds", [{
    severity: monaco.MarkerSeverity.Error,
    message: error.text,
    startLineNumber: line, startColumn: column,
    endLineNumber: line, endColumn: model.getLineMaxColumn(line),
  }]);
}
```

Bind the formatter to `Shift+Alt+F` with `editor.addAction`.

- [ ] **Step 8: Verify by hand**

Type `SELECT * FROM ` and confirm the table list appears; type `u.` after `FROM users u` and confirm
the column list appears; run a query with a syntax error and confirm the red squiggle lands on the
reported line; press Shift+Alt+F and confirm the statement reformats.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: schema-aware completion, hover, formatting and error markers"
```

---

### Task 7: Query history and tab persistence

**Files:**
- Create: `src/WebDataStudio.Server/Services/WorkspaceStore.cs`
- Create: `src/WebDataStudio.Server/Endpoints/WorkspaceEndpoints.cs`
- Modify: `src/WebDataStudio.Server/Program.cs`
- Create: `tests/WebDataStudio.Server.Tests/WorkspaceStoreTests.cs`
- Modify: `web/src/api.ts`

**Interfaces:**
- Consumes: the application SQLite database from P0.
- Produces:
  - `WorkspaceStore` with `AddHistory(entry)`, `ListHistory(connectionId?, search?, limit)`, `SaveTabs(json)`, `LoadTabs()`.
  - `record HistoryEntry(long Id, string ConnectionId, string Sql, DateTimeOffset ExecutedAt, long? ElapsedMs, long? RowCount, string? Error)`
  - `GET/POST /api/history`, `GET/PUT /api/workspace/tabs`.
  - `api.ts`: `listHistory(params)`, `addHistory(entry)`, `loadTabs()`, `saveTabs(tabs)`.

- [ ] **Step 1: Write the failing test**

`tests/WebDataStudio.Server.Tests/WorkspaceStoreTests.cs`:

```csharp
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class WorkspaceStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-workspace").FullName;
    private WorkspaceStore NewStore() => new(Path.Combine(_dir, "wds.db"));
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Records_and_lists_history_newest_first()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELECT 1", 10, 1, null);
        store.AddHistory("c1", "SELECT 2", 20, 1, null);

        var history = store.ListHistory(null, null, 10);
        Assert.Equal("SELECT 2", history[0].Sql);
    }

    [Fact]
    public void Filters_history_by_connection()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELECT 1", 10, 1, null);
        store.AddHistory("c2", "SELECT 2", 10, 1, null);

        Assert.Single(store.ListHistory("c2", null, 10));
    }

    [Fact]
    public void Searches_history_text()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELECT * FROM people", 10, 3, null);
        store.AddHistory("c1", "SELECT * FROM orders", 10, 3, null);

        Assert.Single(store.ListHistory(null, "people", 10));
    }

    [Fact]
    public void Records_a_failed_query_with_its_error()
    {
        var store = NewStore();
        store.AddHistory("c1", "SELCT 1", null, null, "syntax error");
        Assert.Equal("syntax error", store.ListHistory(null, null, 10)[0].Error);
    }

    [Fact]
    public void Tabs_survive_a_reopen()
    {
        NewStore().SaveTabs("""[{"id":"t1","sql":"SELECT 1"}]""");
        Assert.Contains("SELECT 1", NewStore().LoadTabs());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter WorkspaceStore`
Expected: build error — `WorkspaceStore` does not exist.

- [ ] **Step 3: Implement the store**

`src/WebDataStudio.Server/Services/WorkspaceStore.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace WebDataStudio.Server.Services;

public sealed record HistoryEntry(long Id, string ConnectionId, string Sql,
    DateTimeOffset ExecutedAt, long? ElapsedMs, long? RowCount, string? Error);

/// Query history and open tabs. Both live server-side so a container restart does not lose them.
public sealed class WorkspaceStore
{
    private readonly string _connectionString;

    public WorkspaceStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                connection_id TEXT NOT NULL,
                sql TEXT NOT NULL,
                executed_at TEXT NOT NULL,
                elapsed_ms INTEGER NULL,
                row_count INTEGER NULL,
                error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_time ON history(executed_at DESC);
            CREATE TABLE IF NOT EXISTS workspace (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public void AddHistory(string connectionId, string sql, long? elapsedMs, long? rowCount, string? error)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (connection_id, sql, executed_at, elapsed_ms, row_count, error)
            VALUES ($c, $s, $t, $e, $r, $err)
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$s", sql);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$e", (object?)elapsedMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (object?)rowCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> ListHistory(string? connectionId, string? search, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, connection_id, sql, executed_at, elapsed_ms, row_count, error
              FROM history
             WHERE ($c IS NULL OR connection_id = $c)
               AND ($q IS NULL OR sql LIKE '%' || $q || '%')
             ORDER BY id DESC
             LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$c", (object?)connectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$q", (object?)search ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new HistoryEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    public void SaveTabs(string json) => SetValue("tabs", json);
    public string LoadTabs() => GetValue("tabs") ?? "[]";
    public void SaveLayout(string connectionId, string json) => SetValue($"layout:{connectionId}", json);
    public string? LoadLayout(string connectionId) => GetValue($"layout:{connectionId}");

    private void SetValue(string key, string value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspace (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private string? GetValue(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM workspace WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
}
```

- [ ] **Step 4: Add the endpoints**

`src/WebDataStudio.Server/Endpoints/WorkspaceEndpoints.cs` mapping:

```csharp
app.MapGet("/api/history", (string? connectionId, string? search, int? limit, WorkspaceStore store) =>
    Results.Ok(store.ListHistory(connectionId, search, limit ?? 200)));

app.MapPost("/api/history", (HistoryRequest body, WorkspaceStore store) =>
{
    store.AddHistory(body.ConnectionId, body.Sql, body.ElapsedMs, body.RowCount, body.Error);
    return Results.NoContent();
});

app.MapGet("/api/workspace/tabs", (WorkspaceStore store) =>
    Results.Content(store.LoadTabs(), "application/json"));

app.MapPut("/api/workspace/tabs", async (HttpContext ctx, WorkspaceStore store) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    store.SaveTabs(await reader.ReadToEndAsync());
    return Results.NoContent();
});

public record HistoryRequest(string ConnectionId, string Sql, long? ElapsedMs, long? RowCount, string? Error);
```

Register `WorkspaceStore` as a singleton with the same `DB_PATH` the connection store uses, and call
`app.MapWorkspaceEndpoints()`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter WorkspaceStore`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: server-side query history and tab persistence"
```

---

### Task 8: Query tab panel and dock integration

**Files:**
- Create: `web/src/query/QueryTab.tsx`
- Create: `web/src/query/ResultArea.tsx`
- Create: `web/src/query/HistoryPanel.tsx`
- Modify: `web/src/dock/DockShell.tsx`
- Modify: `web/src/api.ts`

**Interfaces:**
- Consumes: `QueryEditor`, `runQuery`, `applyChunk`, `ResultGrid`, history API.
- Produces: `<QueryTab connectionId title initialSql />` — one dockview panel holding an editor above
  and a result area below; `openQueryTab(api, connectionId, sql?)` used by the explorer's context menu
  and the command palette.

- [ ] **Step 1: Write the result area**

`web/src/query/ResultArea.tsx` renders a Mantine `Tabs` with one tab per statement result plus
Messages and History tabs. Each statement tab hosts `<ResultGrid result={statement} />`. The footer
line shows `rows · elapsed · truncated` and an Export button that P3 wires up.

- [ ] **Step 2: Write the query tab**

`web/src/query/QueryTab.tsx`:

```tsx
import { useCallback, useRef, useState } from "react";
import { ActionIcon, Group, Select, Text, Tooltip } from "@mantine/core";
import { IconPlayerPlay, IconPlayerStop, IconPlayerTrackNext } from "@tabler/icons-react";
import { QueryEditor } from "../editor/QueryEditor";
import { ResultArea } from "./ResultArea";
import { runQuery, type QueryRun } from "./runQuery";
import { applyChunk, createResultState, type ResultState } from "./resultStore";
import { addHistory } from "../api";
import type { DialectId } from "../sql/splitStatements";

export function QueryTab({ connectionId, dialect, initialSql = "" }: {
  connectionId: string; dialect: DialectId; initialSql?: string;
}) {
  const [sql, setSql] = useState(initialSql);
  const [result, setResult] = useState<ResultState>(createResultState);
  const [running, setRunning] = useState(false);
  const activeRun = useRef<QueryRun | null>(null);

  const execute = useCallback(async (text: string) => {
    if (!text.trim()) return;
    setResult(createResultState());
    setRunning(true);

    const started = performance.now();
    let state = createResultState();
    const run = runQuery({ connectionId, sql: text }, chunk => {
      state = applyChunk(state, chunk);
      setResult(state);
    });
    activeRun.current = run;

    try {
      await run.done;
    } finally {
      setRunning(false);
      activeRun.current = null;
      const last = state.statements[state.statements.length - 1];
      // History is best-effort: a failed write must never swallow the result the user is reading.
      addHistory({
        connectionId, sql: text,
        elapsedMs: Math.round(performance.now() - started),
        rowCount: last?.rows.length ?? null,
        error: last?.error?.text ?? null,
      }).catch(() => {});
    }
  }, [connectionId]);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <Group gap={4} p={2}>
        <Tooltip label="Run selection or statement (F5)">
          <ActionIcon variant="subtle" disabled={running} onClick={() => execute(sql)}>
            <IconPlayerPlay size={16} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Run whole script (Ctrl+Shift+Enter)">
          <ActionIcon variant="subtle" disabled={running} onClick={() => execute(sql)}>
            <IconPlayerTrackNext size={16} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Cancel">
          <ActionIcon variant="subtle" color="red" disabled={!running}
            onClick={() => activeRun.current?.cancel()}>
            <IconPlayerStop size={16} />
          </ActionIcon>
        </Tooltip>
        {result.cancelled && <Text size="xs" c="orange">cancelled</Text>}
      </Group>

      <div style={{ flex: 1, minHeight: 120 }}>
        <QueryEditor value={sql} dialect={dialect} onChange={setSql}
          onRun={execute} onRunAll={execute} />
      </div>
      <div style={{ flex: 1, minHeight: 120, borderTop: "1px solid var(--mantine-color-default-border)" }}>
        <ResultArea result={result} />
      </div>
    </div>
  );
}
```

- [ ] **Step 3: Register the panel type in `DockShell`**

Add `query: (props) => <QueryTab {...props.params} />` to the dockview `components` map, an
`openQueryTab` helper that calls `api.addPanel({ id, component: "query", title, params })`, a "New
query" toolbar button, and a "New query here" item in the explorer context menu that pre-fills
`SELECT * FROM <table>`.

- [ ] **Step 4: Persist and restore tabs**

On every dockview layout change, `saveTabs(JSON.stringify(api.toJSON()))` debounced by 500 ms; on
mount, `loadTabs()` and `api.fromJSON(...)` inside a `try`/`catch` that falls back to a fresh layout
if the stored JSON no longer matches the panel components.

- [ ] **Step 5: Write the history panel**

`web/src/query/HistoryPanel.tsx` lists entries from `listHistory`, with a search box, the elapsed
time and row count per entry, a red marker for failed ones, and a click that opens a new query tab
pre-filled with that SQL.

- [ ] **Step 6: Verify by hand**

Open a query tab against the SQLite demo connection, run `SELECT * FROM people`, confirm rows appear
in the grid, sort and filter a column, select several numeric cells and read the sum in the status
bar, cancel a long query, reload the page and confirm the tab and its SQL come back.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: query tabs with results, history and tab persistence"
```

---

## Phase exit criteria

- `dotnet test` and `npx vitest run` both pass.
- F5 runs the selection when there is one and the statement under the cursor otherwise, with the
  active statement visibly highlighted.
- Completion offers tables after FROM and columns after an alias dot, against a live connection.
- A 100k-row result scrolls smoothly; sorting, filtering, column hiding and the selection aggregate
  all work.
- A syntax error marks the reported line in Monaco.
- Query history persists across a container restart, and open tabs come back after a reload.
- Feature IDs F3.1–F3.7, F3.10, F3.12, F3.13, F4.8 and F5.1–F5.7 are demonstrably working.
