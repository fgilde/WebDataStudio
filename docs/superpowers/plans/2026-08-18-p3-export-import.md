# P3 — Export and Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Get any result, table or schema out of the tool in every format that makes sense, and get CSV, Excel, JSON and SQL back in — without the server ever holding a whole data set in memory.

**Architecture:** One `IResultExporter` per format, all fed by the same `IAsyncEnumerable<ResultChunk>` the query endpoint already produces. The HTTP response streams as the driver reads, so a 10-million-row export costs constant memory. Import is the mirror: a streaming reader plus a column-mapping step the user confirms before anything is written.

**Tech Stack:** CsvHelper, MiniExcel (streaming xlsx), Parquet.Net, YamlDotNet, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`

## Global Constraints

- Everything from P0–P2 global constraints still holds.
- No exporter may buffer the full result. Every exporter writes to the response stream chunk by chunk, and a test asserts memory stays flat for a large synthetic result.
- Export never includes connection secrets, and an export of a read-only connection is still allowed (reading is not writing).
- Import writes through the driver's normal execution path, so a read-only connection refuses it.
- Feature IDs delivered by this phase: F7.1–F7.7.

---

### Task 1: Exporter abstraction and CSV/TSV

**Files:**
- Create: `src/WebDataStudio.Server/Export/IResultExporter.cs`
- Create: `src/WebDataStudio.Server/Export/ExportOptions.cs`
- Create: `src/WebDataStudio.Server/Export/DelimitedExporter.cs`
- Create: `src/WebDataStudio.Server/Export/ExporterRegistry.cs`
- Create: `tests/WebDataStudio.Server.Tests/Export/DelimitedExporterTests.cs`

**Interfaces:**
- Consumes: `ResultChunk` from P1.
- Produces:
  - `record ExportOptions(string Delimiter, string Encoding, bool Header, string NullText, string DateFormat, bool QuoteAll, string? TableName)`
  - `interface IResultExporter { string Format { get; } string ContentType { get; } string FileExtension { get; } Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks, ExportOptions options, CancellationToken ct); }`
  - `ExporterRegistry.Get(string format) -> IResultExporter`, `ExporterRegistry.All()`.

- [ ] **Step 1: Write the failing test**

`tests/WebDataStudio.Server.Tests/Export/DelimitedExporterTests.cs`:

```csharp
using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Export;

namespace WebDataStudio.Server.Tests.Export;

public class DelimitedExporterTests
{
    private static async IAsyncEnumerable<ResultChunk> Sample()
    {
        yield return new ResultChunk.Columns(0, [
            new ColumnMeta("id", "int", false),
            new ColumnMeta("name", "text", true),
        ]);
        yield return new ResultChunk.Rows(0, [[1, "ada"], [2, null], [3, "say \"hi\""]]);
        yield return new ResultChunk.End(0, 0, 1, false);
        await Task.CompletedTask;
    }

    private static async Task<string> ExportAsync(IResultExporter exporter, ExportOptions options)
    {
        using var stream = new MemoryStream();
        await exporter.WriteAsync(stream, Sample(), options, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Writes_a_header_and_rows()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default);
        Assert.StartsWith("id,name", csv);
        Assert.Contains("1,ada", csv);
    }

    [Fact]
    public async Task Renders_null_as_the_configured_text()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default with { NullText = "\\N" });
        Assert.Contains("2,\\N", csv);
    }

    [Fact]
    public async Task Quotes_a_value_containing_a_quote()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default);
        Assert.Contains("\"say \"\"hi\"\"\"", csv);
    }

    [Fact]
    public async Task Omits_the_header_when_asked()
    {
        var csv = await ExportAsync(new DelimitedExporter("csv", ","), ExportOptions.Default with { Header = false });
        Assert.StartsWith("1,ada", csv);
    }

    [Fact]
    public async Task Tsv_uses_tabs()
    {
        var tsv = await ExportAsync(new DelimitedExporter("tsv", "\t"), ExportOptions.Default);
        Assert.Contains("1\tada", tsv);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter DelimitedExporter`
Expected: build error — the export namespace does not exist.

- [ ] **Step 3: Write the abstraction**

`Export/ExportOptions.cs`:

```csharp
namespace WebDataStudio.Server.Export;

public sealed record ExportOptions(
    string Delimiter, string Encoding, bool Header, string NullText,
    string DateFormat, bool QuoteAll, string? TableName)
{
    public static ExportOptions Default { get; } =
        new(",", "utf-8", true, "", "O", false, null);
}
```

`Export/IResultExporter.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Export;

/// Formats a streaming result. Implementations must write as chunks arrive; buffering the whole
/// result defeats the point and is caught by MemoryProfileTests.
public interface IResultExporter
{
    string Format { get; }
    string ContentType { get; }
    string FileExtension { get; }
    Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks, ExportOptions options, CancellationToken ct);
}
```

- [ ] **Step 4: Write the delimited exporter**

`Export/DelimitedExporter.cs` — a `StreamWriter` with `AutoFlush = false`, flushed after every rows
chunk. Escapes a field when it contains the delimiter, a quote, CR or LF, or when `QuoteAll` is set,
doubling embedded quotes. Renders `null` as `options.NullText`, `DateTime`/`DateTimeOffset` with
`options.DateFormat`, `byte[]` as `0x…` hex.

- [ ] **Step 5: Write the registry**

`Export/ExporterRegistry.cs` — a dictionary keyed by format id, seeded with every exporter in this
phase. `Get` throws `NotSupportedException` for an unknown format; `All()` feeds the UI's format
dropdown so a new exporter appears in the menu automatically.

- [ ] **Step 6: Run the tests**

Run: `dotnet test --filter DelimitedExporter`
Expected: PASS, 5 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: streaming export abstraction with CSV and TSV"
```

---

### Task 2: JSON, NDJSON, XML, YAML, Markdown and HTML exporters

**Files:**
- Create: `src/WebDataStudio.Server/Export/JsonExporter.cs`, `NdJsonExporter.cs`, `XmlExporter.cs`, `YamlExporter.cs`, `MarkdownExporter.cs`, `HtmlExporter.cs`
- Create: `tests/WebDataStudio.Server.Tests/Export/TextExporterTests.cs`

**Interfaces:**
- Consumes: `IResultExporter`, `ExportOptions`.
- Produces: six exporters registered under `json`, `ndjson`, `xml`, `yaml`, `markdown`, `html`.

- [ ] **Step 1: Write the failing tests**

One test per format asserting the shape of the same three-row sample:
`JsonExporter` emits `[{"id":1,"name":"ada"},…]` with `null` for the null cell and streams
element-by-element via `Utf8JsonWriter`; `NdJsonExporter` emits one object per line;
`XmlExporter` emits `<rows><row><id>1</id>…` with invalid element-name characters replaced;
`YamlExporter` emits a sequence of mappings; `MarkdownExporter` emits a pipe table with a separator
row; `HtmlExporter` emits a `<table>` with escaped cell content. Each test also asserts an empty
result still produces a valid document (`[]`, `<rows/>`, an empty table).

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter TextExporter`

- [ ] **Step 3: Implement the six exporters**

Each writes through `Utf8JsonWriter`, `XmlWriter` or a plain `StreamWriter` and flushes per chunk.
`MarkdownExporter` must know its column widths before writing the separator row, so it writes the
header from the columns chunk and pads to a fixed width rather than measuring the whole result.

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: JSON, NDJSON, XML, YAML, Markdown and HTML exporters"
```

---

### Task 3: Excel and Parquet exporters

**Files:**
- Create: `src/WebDataStudio.Server/Export/ExcelExporter.cs`
- Create: `src/WebDataStudio.Server/Export/ParquetExporter.cs`
- Create: `tests/WebDataStudio.Server.Tests/Export/BinaryExporterTests.cs`
- Modify: `Directory.Packages.props` (add `MiniExcel`, `Parquet.Net`)

**Interfaces:**
- Consumes: `IResultExporter`.
- Produces: exporters registered under `xlsx` and `parquet`.

- [ ] **Step 1: Write the failing tests**

Assert that the xlsx bytes start with the ZIP magic `PK`, that reading the produced file back with
MiniExcel yields three rows and the header names, and that a value longer than Excel's 32767
character cell limit is truncated with a trailing marker rather than producing a corrupt file.
For Parquet, assert the file starts with `PAR1`, round-trips through `Parquet.Net`, and that an
all-null column produces a nullable string column rather than throwing.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter BinaryExporter`

- [ ] **Step 3: Implement `ExcelExporter`**

MiniExcel's `SaveAsAsync` accepts an `IEnumerable<IDictionary<string, object>>`; feed it a lazy
enumerable that pulls from the chunk stream so rows are written as they arrive. Excel needs a
worksheet name: use `options.TableName ?? "Result"`, stripped of the characters Excel forbids.

- [ ] **Step 4: Implement `ParquetExporter`**

Parquet is columnar, so it cannot be written strictly row-by-row: buffer one row group (default
50 000 rows), write it, then clear. Column types are inferred from the first non-null value per
column, falling back to string. Document the row-group buffer in a comment — it is the one place in
the export path that holds more than a chunk.

- [ ] **Step 5: Run the tests**

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: Excel and Parquet exporters"
```

---

### Task 4: SQL exporters

**Files:**
- Create: `src/WebDataStudio.Server/Export/SqlInsertExporter.cs`
- Create: `src/WebDataStudio.Server/Export/SqlSchemaExporter.cs`
- Create: `tests/WebDataStudio.Server.Tests/Export/SqlExporterTests.cs`

**Interfaces:**
- Consumes: `SqlDialect`, `IDdlWriter` (introduced here as a minimal `CreateTable` only; P6 extends it).
- Produces: exporters `sql-insert` and `sql-create`.

- [ ] **Step 1: Write the failing tests**

Assert that identifiers are quoted with the target dialect (`"users"` for PostgreSQL, `` `users` ``
for MySQL, `[users]` for SQL Server), that a string containing a quote is escaped by doubling, that
`null` becomes the literal `NULL` and not the string `'NULL'`, that numbers are unquoted, that
booleans render per dialect (`TRUE` for PostgreSQL, `1` for SQL Server), and that rows are batched
into multi-row `INSERT` statements of at most 500 value tuples.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter SqlExporter`

- [ ] **Step 3: Implement `SqlInsertExporter`**

Takes the dialect from the export request so a PostgreSQL result can be exported as SQL Server
inserts — that is the point of the feature. Literal rendering lives in one `Literal(object?, SqlDialect)`
method so P4's change-script builder can reuse it.

- [ ] **Step 4: Implement `SqlSchemaExporter`**

Emits `CREATE TABLE` from `ObjectDetail` followed by the inserts. Column types are mapped through a
small `TypeMap` per dialect; an unmapped type falls back to the source type name with a comment
warning, which is honest rather than silently wrong.

- [ ] **Step 5: Run the tests**

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: SQL insert and schema exporters"
```

---

### Task 5: Export endpoint and memory guard

**Files:**
- Create: `src/WebDataStudio.Server/Endpoints/ExportEndpoints.cs`
- Create: `tests/WebDataStudio.Server.Tests/Export/ExportEndpointTests.cs`
- Create: `tests/WebDataStudio.Server.Tests/Export/MemoryProfileTests.cs`
- Modify: `src/WebDataStudio.Server/Program.cs`

**Interfaces:**
- Consumes: `ExporterRegistry`, `SessionFactory`.
- Produces:
  - `POST /api/export/{format}` with body `{ connectionId, sql?, objectRef?, scope, options }` where
    `scope` is one of `result`, `table`, `schema`.
  - `GET /api/export/formats` listing id, label, extension and content type for the UI dropdown.

- [ ] **Step 1: Write the failing tests**

Endpoint tests against the SQLite demo connection: exporting `SELECT * FROM people` as CSV returns
`text/csv` with a `Content-Disposition` filename, an unknown format returns 400, exporting a table by
`objectRef` works without any SQL in the body, and the export of a table on a read-only connection
still succeeds.

`MemoryProfileTests` generates a 200 000-row synthetic chunk stream, exports it to
`Stream.Null`, and asserts `GC.GetTotalAllocatedBytes` growth stays under a threshold proportional
to the chunk size, not to the row count — the test that keeps streaming honest.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter Export`

- [ ] **Step 3: Implement the endpoint**

Resolve the exporter, set `Content-Type` and `Content-Disposition`
(`attachment; filename="<name>-<yyyyMMdd-HHmm>.<ext>"`), then pipe
`driver.ExecuteAsync(...)` straight into `exporter.WriteAsync(Response.Body, …)`. For
`scope: "table"` the SQL is generated as `SELECT * FROM <quoted ref>`; for `scope: "schema"` the
endpoint iterates the schema's tables and writes one section per table (only the SQL and the
Markdown/HTML exporters support this; the others return 400 for that scope).

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: streaming export endpoint"
```

---

### Task 6: Export UI and clipboard actions

**Files:**
- Create: `web/src/export/ExportMenu.tsx`
- Create: `web/src/export/ExportDialog.tsx`
- Create: `web/src/export/copyAs.ts`
- Create: `web/src/export/copyAs.test.ts`
- Modify: `web/src/query/ResultArea.tsx`, `web/src/explorer/ExplorerTree.tsx`

**Interfaces:**
- Consumes: `GET /api/export/formats`, `POST /api/export/{format}`.
- Produces:
  - `copyAsCsv(rows, columns)`, `copyAsJson(rows, columns)`, `copyAsSqlInList(values)`, `copyAsMarkdown(rows, columns)` — all pure string builders, all unit-tested.
  - `<ExportMenu scope … />` — the dropdown used from both the result footer and the explorer context menu.

- [ ] **Step 1: Write the failing test**

`copyAs.test.ts` asserts: CSV quotes a value containing a comma; JSON produces an array of objects
keyed by column name; the SQL IN-list wraps strings in quotes, leaves numbers bare, doubles embedded
quotes and joins with `, `; the Markdown table has a separator row and escapes pipes.

- [ ] **Step 2: Run it to verify it fails**

Run: `cd web && npx vitest run copyAs`

- [ ] **Step 3: Implement `copyAs.ts`**

Pure functions over `unknown[][]` and `QueryColumn[]`, no DOM access, so they stay testable; the
components call `navigator.clipboard.writeText(...)` with the returned string.

- [ ] **Step 4: Run the test**

Expected: PASS.

- [ ] **Step 5: Build the export dialog**

Format dropdown from `GET /api/export/formats`, scope radio group (selection / current page / whole
result / whole table), and an options section that shows only the options the chosen format uses
(delimiter and quoting for CSV, sheet name for xlsx, target dialect for SQL). Download is a plain
`<a download>` pointed at a blob built from the streamed response, so the browser shows real
progress on a large export.

- [ ] **Step 6: Wire the entry points**

The result footer's Export button opens the dialog with `scope: "result"`; the explorer context menu
opens it with `scope: "table"` and the selected object; a Copy submenu offers the four `copyAs`
actions on the current grid selection.

- [ ] **Step 7: Verify by hand**

Export a query result as CSV, xlsx and SQL inserts; open the xlsx in a spreadsheet program; copy a
column selection as an SQL IN-list and paste it into a query.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: export dialog and clipboard actions"
```

---

### Task 7: Import

**Files:**
- Create: `src/WebDataStudio.Server/Import/ImportPreview.cs`
- Create: `src/WebDataStudio.Server/Import/CsvImporter.cs`, `ExcelImporter.cs`, `JsonImporter.cs`, `SqlScriptImporter.cs`
- Create: `src/WebDataStudio.Server/Endpoints/ImportEndpoints.cs`
- Create: `tests/WebDataStudio.Server.Tests/Import/ImportTests.cs`
- Create: `web/src/import/ImportDialog.tsx`

**Interfaces:**
- Consumes: `SessionFactory`, `SqlDialect`.
- Produces:
  - `POST /api/import/preview` → `{ columns: string[], sampleRows: string[][], detectedTypes: string[], suggestedMapping: Record<string,string> }`
  - `POST /api/import/execute` → `{ inserted: number, failed: number, errors: string[] }`
  - `POST /api/copy-table` → table-to-table copy, including across connections and engines.

- [ ] **Step 1: Write the failing tests**

Preview of a CSV with a header returns its column names and the first 20 rows; preview of a CSV
without a header returns positional names; a column of digits is detected as integer and a column of
ISO dates as timestamp; execute inserts three rows into an existing table; a row that violates a
constraint is reported in `errors` while the rest still import (batch size 500, per-batch
transaction); import against a read-only connection returns 403; `copy-table` moves the seeded
`people` table from SQLite into PostgreSQL with matching row count.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter Import`

- [ ] **Step 3: Implement the importers**

Each importer exposes `PreviewAsync(Stream, ImportSettings)` and
`IAsyncEnumerable<object?[]> ReadAsync(Stream, ImportSettings)`. `SqlScriptImporter` reuses
`StatementSplitter`, so a dumped script imports statement by statement with a running progress count
rather than as one giant command.

- [ ] **Step 4: Implement `copy-table`**

Opens both sessions, reads the source through the normal streaming path, and writes through batched
parameterised inserts built from the target dialect. Type mapping goes through the same `TypeMap`
the SQL schema exporter uses.

- [ ] **Step 5: Build the import dialog**

File picker, format detection from the extension, preview table, a mapping row per source column
(target column dropdown plus "skip"), a target-table selector with "create new table" as an option,
and a summary screen after the run.

- [ ] **Step 6: Run the tests and verify by hand**

Import a CSV into the SQLite demo database, then copy that table into PostgreSQL and confirm the row
counts match.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: CSV, Excel, JSON and SQL import plus cross-engine table copy"
```

---

## Phase exit criteria

- Every format in F7.1 exports a correct file from a live query result.
- `MemoryProfileTests` proves the export path does not scale allocations with row count.
- Export scopes selection, page, result and table all work; schema scope works for the formats that
  support it and returns a clear 400 for those that do not.
- The four clipboard actions produce pasteable text.
- CSV, Excel, JSON and SQL import work with column mapping and a preview, and cross-engine table
  copy moves data between two different engines.
- Feature IDs F7.1–F7.7 are demonstrably working.
