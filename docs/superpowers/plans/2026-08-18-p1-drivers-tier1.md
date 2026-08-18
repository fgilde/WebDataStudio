# P1 — Driver Abstraction and Tier 1 Engines Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Browse and query PostgreSQL, MySQL/MariaDB, SQL Server and SQLite through one driver interface, with a live object explorer and streaming query execution.

**Architecture:** One `IDbDriver` per engine behind a registry. A shared xUnit contract suite runs the same assertions against every driver, using Testcontainers for the server engines and a temp file for SQLite. Results stream from the driver as `ResultChunk` values and reach the browser as NDJSON.

**Tech Stack:** Npgsql, MySqlConnector, Microsoft.Data.SqlClient, Microsoft.Data.Sqlite, Testcontainers, React 19 + Mantine 9 + dockview.

**Spec:** `docs/superpowers/specs/2026-08-18-webdatastudio-design.md`

## Global Constraints

- Everything from P0's global constraints still holds.
- No engine-specific branching outside `Drivers/<Engine>/`. Endpoints and UI read `DriverCapabilities`.
- A capability set to `true` must work; a capability set to `false` must throw `NotSupportedException`, never fail obscurely. This is asserted by a test.
- Results are streamed. No endpoint materialises a full result set in memory.
- A read-only connection rejects DML and DDL in the driver, not in the client.
- Feature IDs delivered by this phase: F2.1–F2.6, F4.1–F4.4, F4.6.

---

### Task 1: Driver abstractions

**Files:**
- Create: `src/WebDataStudio.Server/Drivers/Abstractions/DriverInfo.cs`
- Create: `src/WebDataStudio.Server/Drivers/Abstractions/DriverCapabilities.cs`
- Create: `src/WebDataStudio.Server/Drivers/Abstractions/SqlDialect.cs`
- Create: `src/WebDataStudio.Server/Drivers/Abstractions/SchemaModel.cs`
- Create: `src/WebDataStudio.Server/Drivers/Abstractions/ResultModel.cs`
- Create: `src/WebDataStudio.Server/Drivers/Abstractions/IDbDriver.cs`
- Create: `src/WebDataStudio.Server/Drivers/DriverRegistry.cs`
- Create: `tests/WebDataStudio.Server.Tests/DriverRegistryTests.cs`

**Interfaces:**
- Consumes: `ConnectionSpec` from P0 Task 4.
- Produces: every type below. Tasks 2–9 and phases P3–P9 all build on these exact names.

- [ ] **Step 1: Write the failing test**

`tests/WebDataStudio.Server.Tests/DriverRegistryTests.cs`:

```csharp
using WebDataStudio.Server.Drivers;

namespace WebDataStudio.Server.Tests;

public class DriverRegistryTests
{
    [Theory]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void Resolves_every_tier_one_engine(string engine) =>
        Assert.NotNull(new DriverRegistry().Get(engine));

    [Fact]
    public void Unknown_engine_throws() =>
        Assert.Throws<NotSupportedException>(() => new DriverRegistry().Get("notadb"));

    [Fact]
    public void Driver_ids_match_their_registry_key()
    {
        var registry = new DriverRegistry();
        foreach (var driver in registry.All())
            Assert.Same(driver, registry.Get(driver.Info.Id));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter DriverRegistry`
Expected: build error — `DriverRegistry` does not exist.

- [ ] **Step 3: Write the descriptive types**

`Drivers/Abstractions/DriverInfo.cs`:

```csharp
namespace WebDataStudio.Server.Drivers.Abstractions;

/// Static description of an engine, used by the connection form and the UI icons.
public sealed record DriverInfo(
    string Id,
    string Label,
    int DefaultPort,
    string ConnectionStringTemplate);
```

`Drivers/Abstractions/DriverCapabilities.cs`:

```csharp
namespace WebDataStudio.Server.Drivers.Abstractions;

/// What an engine can do. The UI hides everything set to false, and a driver must throw
/// NotSupportedException for anything it declares false — asserted by the contract suite.
public sealed record DriverCapabilities
{
    public bool Sql { get; init; } = true;
    public bool MultiSchema { get; init; }
    public bool MultiDatabase { get; init; }
    public bool EstimatedPlan { get; init; }
    public bool ActualPlan { get; init; }
    public bool Transactions { get; init; }
    public bool Ddl { get; init; }
    public bool StoredProcedures { get; init; }
    public bool Triggers { get; init; }
    public bool Views { get; init; }
    public bool MaterializedViews { get; init; }
    public bool Sequences { get; init; }
    public bool ForeignKeys { get; init; }
    public bool PartialIndexes { get; init; }
    public bool IncludeColumns { get; init; }
    public bool Backup { get; init; }
    public bool Restore { get; init; }
    public bool UserManagement { get; init; }
    public bool SessionList { get; init; }
    public bool KillSession { get; init; }
    public bool ServerStats { get; init; }
    public bool SlowQueryLog { get; init; }
    public bool SystemCommands { get; init; }
}
```

`Drivers/Abstractions/SqlDialect.cs`:

```csharp
namespace WebDataStudio.Server.Drivers.Abstractions;

/// Everything the formatter, the DDL writer and the paging code need to know about syntax.
public abstract class SqlDialect
{
    public abstract string QuoteIdentifier(string name);
    public abstract string ParameterPrefix { get; }
    /// True when this engine separates batches with a standalone GO line (SQL Server).
    public virtual bool UsesGoBatchSeparator => false;
    /// Wraps a SELECT so the server returns at most `limit` rows starting at `offset`.
    public abstract string Paginate(string sql, int offset, int limit);

    public string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    /// Classifies a statement so a read-only connection can refuse anything that writes.
    public virtual bool IsReadOnlyStatement(string sql)
    {
        var head = sql.TrimStart();
        // Strip leading comments so "-- comment\nDELETE ..." is not mistaken for a read.
        while (head.StartsWith("--") || head.StartsWith("/*"))
        {
            var end = head.StartsWith("--")
                ? head.IndexOf('\n')
                : head.IndexOf("*/", StringComparison.Ordinal) + 1;
            if (end <= 0) return false;
            head = head[(end + 1)..].TrimStart();
        }

        return head.StartsWith("select", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("with", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("show", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("explain", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("describe", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("pragma", StringComparison.OrdinalIgnoreCase);
    }
}
```

`Drivers/Abstractions/SchemaModel.cs`:

```csharp
namespace WebDataStudio.Server.Drivers.Abstractions;

public enum SchemaNodeKind
{
    Database, Schema, TableFolder, ViewFolder, ProcedureFolder, FunctionFolder,
    TriggerFolder, SequenceFolder, Table, View, MaterializedView, Procedure,
    Function, Trigger, Sequence, Column, Index, ForeignKey,
}

/// Identifies an object across the whole tree. `Path` is the ordered chain of names from the
/// database down to the object, which every driver can turn back into a qualified name.
public sealed record SchemaNodeRef(SchemaNodeKind Kind, IReadOnlyList<string> Path)
{
    public string Name => Path.Count == 0 ? "" : Path[^1];
    public override string ToString() => $"{Kind}:{string.Join('/', Path)}";

    public static SchemaNodeRef Parse(string value)
    {
        var split = value.Split(':', 2);
        return new SchemaNodeRef(Enum.Parse<SchemaNodeKind>(split[0]), split[1].Split('/'));
    }
}

public sealed record SchemaNode(
    SchemaNodeRef Ref,
    string Label,
    bool HasChildren,
    string? Detail = null);

public sealed record ColumnInfo(
    string Name, string DataType, bool Nullable, string? Default,
    bool IsPrimaryKey, bool IsIdentity, string? Comment, int Position);

public sealed record IndexInfo(
    string Name, IReadOnlyList<string> Columns, bool Unique, bool Primary, string? Filter);

public sealed record ForeignKeyInfo(
    string Name, IReadOnlyList<string> Columns,
    string ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns,
    string OnDelete, string OnUpdate);

public sealed record TriggerInfo(string Name, string Timing, string Event);

public sealed record ObjectDetail(
    SchemaNodeRef Ref,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<TriggerInfo> Triggers,
    long? RowCount,
    long? SizeBytes,
    string? Comment,
    string? Ddl);
```

`Drivers/Abstractions/ResultModel.cs`:

```csharp
namespace WebDataStudio.Server.Drivers.Abstractions;

public sealed record ColumnMeta(string Name, string DataType, bool Nullable);

public abstract record ResultChunk(int Statement)
{
    public sealed record Columns(int Statement, IReadOnlyList<ColumnMeta> Items) : ResultChunk(Statement);
    public sealed record Rows(int Statement, IReadOnlyList<object?[]> Items) : ResultChunk(Statement);
    public sealed record Progress(int Statement, long RowsRead, long ElapsedMs) : ResultChunk(Statement);
    public sealed record Message(int Statement, string Severity, string Text) : ResultChunk(Statement);
    public sealed record End(int Statement, long RowsAffected, long ElapsedMs, bool Truncated) : ResultChunk(Statement);
    public sealed record Error(int Statement, string Text, string? Code, int? Line, int? Column) : ResultChunk(Statement);
}

public sealed record ScriptRequest(
    string Sql,
    int MaxRows,
    int TimeoutSeconds,
    string? Schema = null,
    IReadOnlyDictionary<string, string?>? Parameters = null);

public enum PlanMode { Estimated, Actual }

public sealed record PlanNode(
    string Operation,
    string? Detail,
    double? EstimatedCost,
    double? EstimatedRows,
    double? ActualRows,
    double? ActualMs,
    IReadOnlyList<PlanNode> Children,
    IReadOnlyList<string> Warnings);

public enum AnalyzeScope { Connection, Schema, Table, Query }

public sealed record AnalyzeFinding(
    string Category, string Severity, string Title, string Detail, string? Statement);

public sealed record AnalyzeReport(IReadOnlyList<AnalyzeFinding> Findings);
```

- [ ] **Step 4: Write the driver interface**

`Drivers/Abstractions/IDbDriver.cs`:

```csharp
using System.Data.Common;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.Abstractions;

/// An open connection to one database. Disposing it returns the underlying connection.
public interface IDbSession : IAsyncDisposable
{
    ConnectionSpec Spec { get; }
    DbConnection Connection { get; }
}

public interface IDbDriver
{
    DriverInfo Info { get; }
    DriverCapabilities Caps { get; }
    SqlDialect Dialect { get; }

    Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct);

    /// One level of the object tree. `parent` is null for the root of the connection.
    Task<IReadOnlyList<SchemaNode>> IntrospectAsync(IDbSession session, SchemaNodeRef? parent, CancellationToken ct);

    Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct);

    IAsyncEnumerable<ResultChunk> ExecuteAsync(IDbSession session, ScriptRequest request, CancellationToken ct);

    Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct);

    Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope, SchemaNodeRef? target, CancellationToken ct);
}
```

The spec's `IDdlWriter Ddl` property is deliberately absent here: nothing in P1 writes DDL. P3 Task 4
adds it with a `CreateTable`-only writer for the SQL schema exporter, and P6 Task 2 grows it into the
full interface. Adding it now would mean four empty implementations that no test can exercise.

- [ ] **Step 5: Write the registry**

`Drivers/DriverRegistry.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Drivers.SqlServer;

namespace WebDataStudio.Server.Drivers;

public sealed class DriverRegistry
{
    private readonly Dictionary<string, IDbDriver> _drivers;

    public DriverRegistry()
    {
        IDbDriver[] drivers =
        [
            new PostgreSqlDriver(),
            new MySqlDriver(),
            new SqlServerDriver(),
            new SqliteDriver(),
        ];
        _drivers = drivers.ToDictionary(d => d.Info.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IDbDriver> All() => _drivers.Values;

    public IDbDriver Get(string engine) =>
        _drivers.TryGetValue(engine, out var driver)
            ? driver
            : throw new NotSupportedException($"no driver for engine '{engine}'");
}
```

The four driver classes arrive in Tasks 3–6. To keep this task's test runnable on its own, create
each file now with a class that throws `NotImplementedException` from every member except `Info`,
`Caps` and `Dialect`, then fill them in. Each subsequent task replaces exactly one of them.

- [ ] **Step 6: Run the test**

Run: `dotnet test --filter DriverRegistry`
Expected: PASS, 6 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: driver abstraction and registry"
```

---

### Task 2: Statement splitter

**Files:**
- Create: `src/WebDataStudio.Server/Services/StatementSplitter.cs`
- Create: `tests/WebDataStudio.Server.Tests/StatementSplitterTests.cs`

**Interfaces:**
- Consumes: `SqlDialect` from Task 1.
- Produces: `StatementSplitter.Split(string sql, SqlDialect dialect) -> IReadOnlyList<Statement>` where
  `record Statement(string Text, int StartOffset, int EndOffset, int StartLine)`. The offsets let the
  editor map an error back to a character position, and P2 reuses the same splitter shape in TypeScript.

- [ ] **Step 1: Write the failing tests**

`tests/WebDataStudio.Server.Tests/StatementSplitterTests.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

public class StatementSplitterTests
{
    private static readonly SqlDialect Postgres = new PostgreSqlDialect();
    private static readonly SqlDialect SqlServer = new SqlServerDialect();

    private static string[] Split(string sql, SqlDialect dialect) =>
        StatementSplitter.Split(sql, dialect).Select(s => s.Text.Trim()).ToArray();

    [Fact]
    public void Splits_on_semicolons() =>
        Assert.Equal(["SELECT 1", "SELECT 2"], Split("SELECT 1; SELECT 2;", Postgres));

    [Fact]
    public void Ignores_a_semicolon_inside_a_string_literal() =>
        Assert.Single(Split("SELECT 'a;b'", Postgres));

    [Fact]
    public void Ignores_a_semicolon_inside_a_line_comment() =>
        Assert.Single(Split("SELECT 1 -- a;b\n", Postgres));

    [Fact]
    public void Ignores_a_semicolon_inside_a_block_comment() =>
        Assert.Single(Split("SELECT /* a;b */ 1", Postgres));

    [Fact]
    public void Ignores_a_semicolon_inside_a_quoted_identifier() =>
        Assert.Single(Split("SELECT \"we;ird\" FROM t", Postgres));

    [Fact]
    public void Keeps_a_dollar_quoted_body_intact()
    {
        var sql = "CREATE FUNCTION f() RETURNS int AS $$ BEGIN SELECT 1; RETURN 2; END $$ LANGUAGE plpgsql;";
        Assert.Single(Split(sql, Postgres));
    }

    [Fact]
    public void Splits_sqlserver_batches_on_go()
    {
        var sql = "SELECT 1\nGO\nSELECT 2\n";
        Assert.Equal(["SELECT 1", "SELECT 2"], Split(sql, SqlServer));
    }

    [Fact]
    public void Does_not_treat_go_inside_an_identifier_as_a_batch_separator() =>
        Assert.Single(Split("SELECT going FROM t", SqlServer));

    [Fact]
    public void Drops_empty_statements() =>
        Assert.Single(Split("SELECT 1;;;", Postgres));

    [Fact]
    public void Reports_the_offset_and_line_of_each_statement()
    {
        var statements = StatementSplitter.Split("SELECT 1;\nSELECT 2;", Postgres);
        Assert.Equal(0, statements[0].StartOffset);
        Assert.Equal(1, statements[0].StartLine);
        Assert.Equal(2, statements[1].StartLine);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter StatementSplitter`
Expected: build error — `StatementSplitter` does not exist.

- [ ] **Step 3: Implement the splitter**

`src/WebDataStudio.Server/Services/StatementSplitter.cs`:

```csharp
using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

public sealed record Statement(string Text, int StartOffset, int EndOffset, int StartLine);

/// Splits a script into executable statements. A character scanner, not a parser: it only needs
/// to know where strings, comments, quoted identifiers and dollar-quoted bodies begin and end.
public static class StatementSplitter
{
    public static IReadOnlyList<Statement> Split(string sql, SqlDialect dialect)
    {
        var statements = new List<Statement>();
        var current = new StringBuilder();
        var start = 0;
        var line = 1;
        var startLine = 1;
        var i = 0;

        void Flush(int end)
        {
            var text = current.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                statements.Add(new Statement(text, start, end, startLine));
            current.Clear();
            start = end + 1;
            startLine = line;
        }

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '\n') line++;

            // line comment
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') current.Append(sql[i++]);
                continue;
            }

            // block comment
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                current.Append(sql[i++]);
                current.Append(sql[i++]);
                while (i < sql.Length && !(sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/'))
                {
                    if (sql[i] == '\n') line++;
                    current.Append(sql[i++]);
                }
                if (i < sql.Length) { current.Append(sql[i++]); current.Append(sql[i++]); }
                continue;
            }

            // string literal or quoted identifier
            if (c is '\'' or '"' or '`' or '[')
            {
                var close = c switch { '[' => ']', _ => c };
                current.Append(sql[i++]);
                while (i < sql.Length)
                {
                    if (sql[i] == '\n') line++;
                    // doubled quote is an escaped quote, not a terminator
                    if (sql[i] == close && i + 1 < sql.Length && sql[i + 1] == close)
                    {
                        current.Append(sql[i++]);
                        current.Append(sql[i++]);
                        continue;
                    }
                    if (sql[i] == close) { current.Append(sql[i++]); break; }
                    current.Append(sql[i++]);
                }
                continue;
            }

            // dollar-quoted body: $$ ... $$ or $tag$ ... $tag$
            if (c == '$')
            {
                var close = sql.IndexOf('$', i + 1);
                if (close > i)
                {
                    var tag = sql[i..(close + 1)];
                    var end = sql.IndexOf(tag, close + 1, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        var body = sql[i..(end + tag.Length)];
                        line += body.Count(ch => ch == '\n');
                        current.Append(body);
                        i = end + tag.Length;
                        continue;
                    }
                }
            }

            // SQL Server batch separator: a line containing only GO
            if (dialect.UsesGoBatchSeparator && (c is 'g' or 'G') && IsGoLine(sql, i, out var afterGo))
            {
                Flush(i - 1);
                line++;
                i = afterGo;
                continue;
            }

            if (c == ';')
            {
                Flush(i);
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        Flush(sql.Length);
        return statements;
    }

    /// True when position `i` starts a standalone GO line (only whitespace before it on the line
    /// and nothing but whitespace after it until the newline).
    private static bool IsGoLine(string sql, int i, out int afterGo)
    {
        afterGo = i;
        var lineStart = sql.LastIndexOf('\n', Math.Max(i - 1, 0));
        var before = sql[(lineStart + 1)..i];
        if (before.Trim().Length != 0) return false;
        if (i + 2 > sql.Length) return false;
        if (!sql.AsSpan(i, 2).Equals("GO", StringComparison.OrdinalIgnoreCase)) return false;

        var j = i + 2;
        while (j < sql.Length && sql[j] is ' ' or '\t' or '\r') j++;
        if (j < sql.Length && sql[j] != '\n') return false;

        afterGo = j < sql.Length ? j + 1 : j;
        return true;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter StatementSplitter`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: dialect-aware statement splitter"
```

---

### Task 3: SQLite driver and the shared contract suite

**Files:**
- Create: `src/WebDataStudio.Server/Drivers/Sqlite/SqliteDialect.cs`
- Create: `src/WebDataStudio.Server/Drivers/Sqlite/SqliteDriver.cs`
- Create: `src/WebDataStudio.Server/Drivers/Abstractions/AdoDriverBase.cs`
- Create: `tests/WebDataStudio.Server.Tests/Drivers/DriverContractTests.cs`
- Create: `tests/WebDataStudio.Server.Tests/Drivers/IDriverFixture.cs`
- Create: `tests/WebDataStudio.Server.Tests/Drivers/SqliteFixture.cs`

**Interfaces:**
- Consumes: everything from Task 1.
- Produces:
  - `AdoDriverBase` with a protected `StreamAsync(DbCommand, int statementIndex, int maxRows, CancellationToken)` helper that every ADO driver reuses for chunked reading.
  - `IDriverFixture` with `IDbDriver Driver { get; }`, `ConnectionSpec Spec { get; }`, `Task SeedAsync()` — Tasks 4, 5 and 6 each add one implementation and the contract suite picks it up automatically.

- [ ] **Step 1: Write the contract suite**

`tests/WebDataStudio.Server.Tests/Drivers/IDriverFixture.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

/// A live database an engine's contract test can run against. Each implementation seeds the same
/// two tables so one assertion set works for every engine.
public interface IDriverFixture : IAsyncLifetime
{
    IDbDriver Driver { get; }
    ConnectionSpec Spec { get; }
    /// The schema the seeded tables live in, or null for engines without schemas.
    string? Schema { get; }
}
```

`tests/WebDataStudio.Server.Tests/Drivers/DriverContractTests.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Drivers;

/// The single behaviour suite every engine must satisfy. Derive one class per engine; the fixture
/// seeds a `people` table (id, name, active) with three rows and an `orders` table with a foreign
/// key to it.
public abstract class DriverContractTests<TFixture> : IClassFixture<TFixture>
    where TFixture : class, IDriverFixture
{
    private readonly TFixture _fixture;
    protected DriverContractTests(TFixture fixture) => _fixture = fixture;

    private IDbDriver Driver => _fixture.Driver;

    private async Task<IDbSession> OpenAsync() =>
        await Driver.OpenAsync(_fixture.Spec, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Opens_a_session()
    {
        await using var session = await OpenAsync();
        Assert.Equal(System.Data.ConnectionState.Open, session.Connection.State);
    }

    [Fact]
    public async Task Root_introspection_returns_children()
    {
        await using var session = await OpenAsync();
        var nodes = await Driver.IntrospectAsync(session, null, TestContext.Current.CancellationToken);
        Assert.NotEmpty(nodes);
    }

    [Fact]
    public async Task Finds_the_seeded_table()
    {
        await using var session = await OpenAsync();
        var table = await FindPeopleAsync(session);
        Assert.NotNull(table);
    }

    [Fact]
    public async Task Describes_columns_with_the_primary_key_marked()
    {
        await using var session = await OpenAsync();
        var detail = await Driver.DescribeAsync(session, (await FindPeopleAsync(session))!.Ref,
            TestContext.Current.CancellationToken);

        Assert.Contains(detail.Columns, c => c.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(detail.Columns, c => c.IsPrimaryKey);
    }

    [Fact]
    public async Task Describes_the_foreign_key_of_the_orders_table()
    {
        if (!Driver.Caps.ForeignKeys) return;

        await using var session = await OpenAsync();
        var orders = await FindObjectAsync(session, "orders");
        var detail = await Driver.DescribeAsync(session, orders!.Ref, TestContext.Current.CancellationToken);

        var fk = Assert.Single(detail.ForeignKeys);
        Assert.Equal("people", fk.ReferencedTable, ignoreCase: true);
    }

    [Fact]
    public async Task Executes_a_select_and_streams_columns_then_rows()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT 1 AS one");

        Assert.Contains(chunks, c => c is ResultChunk.Columns);
        var rows = chunks.OfType<ResultChunk.Rows>().SelectMany(r => r.Items).ToList();
        Assert.Equal(1, Convert.ToInt32(Assert.Single(rows)[0]));
        Assert.Contains(chunks, c => c is ResultChunk.End);
    }

    [Fact]
    public async Task Reads_all_three_seeded_rows()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT id FROM people");
        Assert.Equal(3, chunks.OfType<ResultChunk.Rows>().Sum(r => r.Items.Count));
    }

    [Fact]
    public async Task Honours_the_row_cap_and_flags_truncation()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT id FROM people", maxRows: 2);

        Assert.Equal(2, chunks.OfType<ResultChunk.Rows>().Sum(r => r.Items.Count));
        Assert.True(chunks.OfType<ResultChunk.End>().Single().Truncated);
    }

    [Fact]
    public async Task Executes_several_statements_and_numbers_them()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT 1; SELECT 2;");
        Assert.Equal(2, chunks.OfType<ResultChunk.End>().Count());
        Assert.Contains(chunks, c => c.Statement == 1);
    }

    [Fact]
    public async Task Reports_a_syntax_error_as_an_error_chunk()
    {
        await using var session = await OpenAsync();
        var chunks = await CollectAsync(session, "SELECT FROM WHERE");
        Assert.Contains(chunks, c => c is ResultChunk.Error);
    }

    [Fact]
    public async Task A_read_only_connection_rejects_a_write()
    {
        await using var session = await Driver.OpenAsync(_fixture.Spec with { ReadOnly = true },
            TestContext.Current.CancellationToken);
        var chunks = await CollectAsync(session, "DELETE FROM people");

        var error = Assert.Single(chunks.OfType<ResultChunk.Error>());
        Assert.Contains("read-only", error.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        await using var session = await OpenAsync();
        using var cts = new CancellationTokenSource();
        var request = new ScriptRequest("SELECT id FROM people", MaxRows: 1000, TimeoutSeconds: 30);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Driver.ExecuteAsync(session, request, cts.Token))
                await cts.CancelAsync();
        });
    }

    [Fact]
    public async Task Explain_returns_a_plan_or_throws_when_unsupported()
    {
        await using var session = await OpenAsync();
        if (Driver.Caps.EstimatedPlan)
        {
            var plan = await Driver.ExplainAsync(session, "SELECT * FROM people", PlanMode.Estimated,
                TestContext.Current.CancellationToken);
            Assert.NotNull(plan);
            Assert.NotEmpty(plan.Operation);
        }
        else
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Driver.ExplainAsync(
                session, "SELECT 1", PlanMode.Estimated, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Actual_plan_is_supported_or_throws()
    {
        await using var session = await OpenAsync();
        if (Driver.Caps.ActualPlan) return;

        await Assert.ThrowsAsync<NotSupportedException>(() => Driver.ExplainAsync(
            session, "SELECT 1", PlanMode.Actual, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dialect_quotes_identifiers_reversibly()
    {
        var quoted = Driver.Dialect.QuoteIdentifier("we ird");
        Assert.Contains("we ird", quoted);
        Assert.NotEqual("we ird", quoted);
    }

    [Fact]
    public void Dialect_classifies_writes_as_not_read_only()
    {
        Assert.True(Driver.Dialect.IsReadOnlyStatement("SELECT 1"));
        Assert.True(Driver.Dialect.IsReadOnlyStatement("-- comment\nSELECT 1"));
        Assert.False(Driver.Dialect.IsReadOnlyStatement("DELETE FROM people"));
        Assert.False(Driver.Dialect.IsReadOnlyStatement("-- comment\nDROP TABLE people"));
    }

    // --- helpers -----------------------------------------------------------

    private async Task<List<ResultChunk>> CollectAsync(IDbSession session, string sql, int maxRows = 1000)
    {
        var chunks = new List<ResultChunk>();
        var request = new ScriptRequest(sql, maxRows, TimeoutSeconds: 30, Schema: _fixture.Schema);
        await foreach (var chunk in Driver.ExecuteAsync(session, request, TestContext.Current.CancellationToken))
            chunks.Add(chunk);
        return chunks;
    }

    private Task<SchemaNode?> FindPeopleAsync(IDbSession session) => FindObjectAsync(session, "people");

    /// Walks the tree breadth-first until it finds a node with the given name. Engines differ in
    /// how deep tables sit, so the contract suite must not assume a fixed depth.
    private async Task<SchemaNode?> FindObjectAsync(IDbSession session, string name)
    {
        var queue = new Queue<SchemaNodeRef?>();
        queue.Enqueue(null);
        var visited = 0;

        while (queue.Count > 0 && visited++ < 200)
        {
            var parent = queue.Dequeue();
            var nodes = await Driver.IntrospectAsync(session, parent, TestContext.Current.CancellationToken);
            foreach (var node in nodes)
            {
                if (node.Ref.Kind == SchemaNodeKind.Table &&
                    node.Ref.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return node;
                if (node.HasChildren && node.Ref.Kind is not (SchemaNodeKind.Table or SchemaNodeKind.View))
                    queue.Enqueue(node.Ref);
            }
        }
        return null;
    }
}
```

- [ ] **Step 2: Write the SQLite fixture**

`tests/WebDataStudio.Server.Tests/Drivers/SqliteFixture.cs`:

```csharp
using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.Sqlite;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public sealed class SqliteFixture : IDriverFixture
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wds-{Guid.NewGuid():N}.db");

    public IDbDriver Driver { get; } = new SqliteDriver();
    public ConnectionSpec Spec => new("t", "test", "sqlite", $"Data Source={_path}",
        false, null, null, ConnectionSource.Stored);
    public string? Schema => null;

    public async ValueTask InitializeAsync()
    {
        await using var db = new SqliteConnection(Spec.ConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, active INTEGER NOT NULL);
            INSERT INTO people (id, name, active) VALUES (1,'ada',1),(2,'linus',1),(3,'grace',0);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, person_id INTEGER NOT NULL REFERENCES people(id), total REAL);
            CREATE INDEX ix_orders_person ON orders(person_id);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
        return ValueTask.CompletedTask;
    }
}

public class SqliteContractTests(SqliteFixture fixture) : DriverContractTests<SqliteFixture>(fixture);
```

- [ ] **Step 3: Run the suite to verify it fails**

Run: `dotnet test --filter SqliteContract`
Expected: FAIL — `SqliteDriver` still throws `NotImplementedException`.

- [ ] **Step 4: Write the ADO base class**

`Drivers/Abstractions/AdoDriverBase.cs`:

```csharp
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Drivers.Abstractions;

public sealed class AdoSession(ConnectionSpec spec, DbConnection connection) : IDbSession
{
    public ConnectionSpec Spec { get; } = spec;
    public DbConnection Connection { get; } = connection;
    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}

/// Shared execution machinery for every ADO.NET driver: statement splitting, read-only
/// enforcement, chunked streaming, error mapping.
public abstract class AdoDriverBase : IDbDriver
{
    private const int ChunkSize = 200;

    public abstract DriverInfo Info { get; }
    public abstract DriverCapabilities Caps { get; }
    public abstract SqlDialect Dialect { get; }

    public abstract Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct);
    public abstract Task<IReadOnlyList<SchemaNode>> IntrospectAsync(IDbSession session, SchemaNodeRef? parent, CancellationToken ct);
    public abstract Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct);

    public virtual Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct) =>
        throw new NotSupportedException($"{Info.Label} does not support execution plans");

    public virtual Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope, SchemaNodeRef? target, CancellationToken ct) =>
        Task.FromResult(new AnalyzeReport([]));

    public async IAsyncEnumerable<ResultChunk> ExecuteAsync(
        IDbSession session, ScriptRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var statements = StatementSplitter.Split(request.Sql, Dialect);

        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];

            if (session.Spec.ReadOnly && !Dialect.IsReadOnlyStatement(statement.Text))
            {
                yield return new ResultChunk.Error(index,
                    "this connection is read-only; the statement was not executed", "WDS_READONLY", null, null);
                yield break;
            }

            await foreach (var chunk in RunOneAsync(session, statement.Text, index, request, ct))
                yield return chunk;
        }
    }

    private async IAsyncEnumerable<ResultChunk> RunOneAsync(
        IDbSession session, string sql, int index, ScriptRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        DbCommand command = session.Connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = request.TimeoutSeconds;
        AddParameters(command, request.Parameters);

        DbDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(ct);
        }
        catch (DbException e)
        {
            var (line, column) = LocateError(e, sql);
            yield return new ResultChunk.Error(index, e.Message, e.SqlState, line, column);
            await command.DisposeAsync();
            yield break;
        }

        await using (reader)
        await using (command)
        {
            do
            {
                if (reader.FieldCount > 0)
                {
                    var columns = new ColumnMeta[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        columns[i] = new ColumnMeta(reader.GetName(i), reader.GetDataTypeName(i), true);
                    yield return new ResultChunk.Columns(index, columns);

                    var buffer = new List<object?[]>(ChunkSize);
                    long read = 0;
                    var truncated = false;

                    while (await reader.ReadAsync(ct))
                    {
                        if (read >= request.MaxRows) { truncated = true; break; }

                        var row = new object?[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                            row[i] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i));
                        buffer.Add(row);
                        read++;

                        if (buffer.Count >= ChunkSize)
                        {
                            yield return new ResultChunk.Rows(index, buffer.ToArray());
                            yield return new ResultChunk.Progress(index, read, watch.ElapsedMilliseconds);
                            buffer.Clear();
                        }
                    }

                    if (buffer.Count > 0) yield return new ResultChunk.Rows(index, buffer.ToArray());
                    yield return new ResultChunk.End(index, reader.RecordsAffected, watch.ElapsedMilliseconds, truncated);
                }
                else
                {
                    yield return new ResultChunk.End(index, reader.RecordsAffected, watch.ElapsedMilliseconds, false);
                }
            }
            while (await reader.NextResultAsync(ct));
        }
    }

    /// Values that do not survive JSON round-tripping become strings the grid can render.
    protected virtual object? Normalize(object value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime dt => dt.ToString("O"),
        DateTimeOffset dto => dto.ToString("O"),
        TimeSpan ts => ts.ToString(),
        _ => value,
    };

    /// Engines that report an error position override this so Monaco can mark the exact spot.
    protected virtual (int? Line, int? Column) LocateError(DbException exception, string sql) => (null, null);

    private void AddParameters(DbCommand command, IReadOnlyDictionary<string, string?>? parameters)
    {
        if (parameters is null) return;
        foreach (var (key, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = key;
            parameter.Value = (object?)value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
```

- [ ] **Step 5: Implement the SQLite dialect and driver**

`Drivers/Sqlite/SqliteDialect.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.Sqlite;

public sealed class SqliteDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public override string ParameterPrefix => "$";
    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";
}
```

`Drivers/Sqlite/SqliteDriver.cs`:

```csharp
using Microsoft.Data.Sqlite;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.Sqlite;

public sealed class SqliteDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } = new("sqlite", "SQLite", 0, "Data Source=/path/to.db");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, Transactions = true, Ddl = true, Views = true, Triggers = true,
        ForeignKeys = true, PartialIndexes = true, EstimatedPlan = true, SystemCommands = true,
    };

    public override SqlDialect Dialect { get; } = new SqliteDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new SqliteConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct)
    {
        // SQLite has one nameless schema: the root shows folders directly.
        if (parent is null)
            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TableFolder, ["main", "tables"]), "Tables", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ViewFolder, ["main", "views"]), "Views", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TriggerFolder, ["main", "triggers"]), "Triggers", true),
            ];

        var type = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => "table",
            SchemaNodeKind.ViewFolder => "view",
            SchemaNodeKind.TriggerFolder => "trigger",
            _ => null,
        };
        if (type is null) return [];

        var kind = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => SchemaNodeKind.Table,
            SchemaNodeKind.ViewFolder => SchemaNodeKind.View,
            _ => SchemaNodeKind.Trigger,
        };

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%' ORDER BY name";
        cmd.Parameters.Add(new SqliteParameter("$type", type));

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            nodes.Add(new SchemaNode(new SchemaNodeRef(kind, ["main", name]), name, false));
        }
        return nodes;
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var table = target.Name;
        var columns = new List<ColumnInfo>();
        var indexes = new List<IndexInfo>();
        var foreignKeys = new List<ForeignKeyInfo>();

        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({Dialect.QuoteIdentifier(table)})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5) > 0, false, null, reader.GetInt32(0)));
        }

        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list({Dialect.QuoteIdentifier(table)})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                indexes.Add(new IndexInfo(reader.GetString(1), [], reader.GetInt32(2) == 1, false, null));
        }

        // index_list gives names only; a second pass fills the columns of each index.
        for (var i = 0; i < indexes.Count; i++)
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = $"PRAGMA index_info({Dialect.QuoteIdentifier(indexes[i].Name)})";
            var cols = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(2)) cols.Add(reader.GetString(2));
            indexes[i] = indexes[i] with { Columns = cols };
        }

        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA foreign_key_list({Dialect.QuoteIdentifier(table)})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                foreignKeys.Add(new ForeignKeyInfo(
                    $"fk_{table}_{reader.GetInt32(0)}", [reader.GetString(3)],
                    "main", reader.GetString(2), [reader.GetString(4)],
                    reader.GetString(6), reader.GetString(5)));
        }

        long? rowCount = null;
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {Dialect.QuoteIdentifier(table)}";
            rowCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        string? ddl = null;
        await using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name = $name";
            cmd.Parameters.Add(new SqliteParameter("$name", table));
            ddl = await cmd.ExecuteScalarAsync(ct) as string;
        }

        return new ObjectDetail(target, columns, indexes, foreignKeys, [], rowCount, null, null, ddl);
    }

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        if (mode == PlanMode.Actual)
            throw new NotSupportedException("SQLite does not produce actual execution plans");

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;

        var children = new List<PlanNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var detail = reader.GetString(3);
            var warnings = detail.Contains("SCAN", StringComparison.OrdinalIgnoreCase)
                ? new[] { "full table scan" } : [];
            children.Add(new PlanNode(detail, null, null, null, null, null, [], warnings));
        }

        return new PlanNode("QUERY PLAN", null, null, null, null, null, children, []);
    }
}
```

- [ ] **Step 6: Run the contract suite**

Run: `dotnet test --filter SqliteContract`
Expected: PASS, all contract tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: SQLite driver and shared driver contract suite"
```

---

### Task 4: PostgreSQL driver

**Files:**
- Create: `src/WebDataStudio.Server/Drivers/PostgreSql/PostgreSqlDialect.cs`
- Create: `src/WebDataStudio.Server/Drivers/PostgreSql/PostgreSqlDriver.cs`
- Create: `tests/WebDataStudio.Server.Tests/Drivers/PostgreSqlFixture.cs`
- Modify: `Directory.Packages.props` (add `Npgsql`, `Testcontainers.PostgreSql`)

**Interfaces:**
- Consumes: `AdoDriverBase`, `DriverContractTests<T>`, `IDriverFixture`.
- Produces: `PostgreSqlDriver`, `PostgreSqlDialect` — referenced by `DriverRegistry` and by the splitter tests.

- [ ] **Step 1: Add the packages**

Add to `Directory.Packages.props`: `Npgsql` (latest 10.x) and, in the test group,
`Testcontainers.PostgreSql`. Reference `Npgsql` from the server project and
`Testcontainers.PostgreSql` from the test project.

- [ ] **Step 2: Write the fixture and the contract subclass**

`tests/WebDataStudio.Server.Tests/Drivers/PostgreSqlFixture.cs`:

```csharp
using Npgsql;
using Testcontainers.PostgreSql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.PostgreSql;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public sealed class PostgreSqlFixture : IDriverFixture
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();

    public IDbDriver Driver { get; } = new PostgreSqlDriver();
    public ConnectionSpec Spec => new("t", "test", "postgresql", _container.GetConnectionString(),
        false, null, null, ConnectionSource.Stored);
    public string? Schema => "public";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = new NpgsqlConnection(Spec.ConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id serial PRIMARY KEY, name text NOT NULL, active boolean NOT NULL);
            INSERT INTO people (name, active) VALUES ('ada', true), ('linus', true), ('grace', false);
            CREATE TABLE orders (id serial PRIMARY KEY,
                                 person_id integer NOT NULL REFERENCES people(id),
                                 total numeric(10,2));
            CREATE INDEX ix_orders_person ON orders(person_id);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class PostgreSqlContractTests(PostgreSqlFixture fixture) : DriverContractTests<PostgreSqlFixture>(fixture);
```

- [ ] **Step 3: Run the suite to verify it fails**

Run: `dotnet test --filter PostgreSqlContract`
Expected: FAIL — `PostgreSqlDriver` still throws `NotImplementedException`.

- [ ] **Step 4: Write the dialect**

`Drivers/PostgreSql/PostgreSqlDialect.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.PostgreSql;

public sealed class PostgreSqlDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public override string ParameterPrefix => "@";
    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";
}
```

- [ ] **Step 5: Write the driver**

`Drivers/PostgreSql/PostgreSqlDriver.cs`:

```csharp
using System.Data.Common;
using System.Text.Json;
using Npgsql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.PostgreSql;

public sealed class PostgreSqlDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } =
        new("postgresql", "PostgreSQL", 5432, "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiSchema = true, MultiDatabase = true, Transactions = true, Ddl = true,
        EstimatedPlan = true, ActualPlan = true, StoredProcedures = true, Triggers = true,
        Views = true, MaterializedViews = true, Sequences = true, ForeignKeys = true,
        PartialIndexes = true, IncludeColumns = true, Backup = true, Restore = true,
        UserManagement = true, SessionList = true, KillSession = true, ServerStats = true,
        SlowQueryLog = true, SystemCommands = true,
    };

    public override SqlDialect Dialect { get; } = new PostgreSqlDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct)
    {
        if (parent is null)
            return await QueryNodesAsync(session, ct,
                """
                SELECT nspname FROM pg_namespace
                 WHERE nspname NOT IN ('pg_catalog','information_schema')
                   AND nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp%'
                 ORDER BY nspname
                """,
                name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [name]), name, true));

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var s = parent.Name;
            return
            [
                Folder(SchemaNodeKind.TableFolder, s, "tables", "Tables"),
                Folder(SchemaNodeKind.ViewFolder, s, "views", "Views"),
                Folder(SchemaNodeKind.ProcedureFolder, s, "procedures", "Procedures"),
                Folder(SchemaNodeKind.FunctionFolder, s, "functions", "Functions"),
                Folder(SchemaNodeKind.SequenceFolder, s, "sequences", "Sequences"),
            ];
        }

        var schema = parent.Path[0];
        return parent.Kind switch
        {
            SchemaNodeKind.TableFolder => await ListAsync(session, ct, schema, SchemaNodeKind.Table,
                "SELECT tablename FROM pg_tables WHERE schemaname = @s ORDER BY tablename"),
            SchemaNodeKind.ViewFolder => await ListAsync(session, ct, schema, SchemaNodeKind.View,
                "SELECT viewname FROM pg_views WHERE schemaname = @s ORDER BY viewname"),
            SchemaNodeKind.ProcedureFolder => await ListAsync(session, ct, schema, SchemaNodeKind.Procedure,
                """
                SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname = @s AND p.prokind = 'p' ORDER BY p.proname
                """),
            SchemaNodeKind.FunctionFolder => await ListAsync(session, ct, schema, SchemaNodeKind.Function,
                """
                SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname = @s AND p.prokind = 'f' ORDER BY p.proname
                """),
            SchemaNodeKind.SequenceFolder => await ListAsync(session, ct, schema, SchemaNodeKind.Sequence,
                "SELECT sequencename FROM pg_sequences WHERE schemaname = @s ORDER BY sequencename"),
            _ => [],
        };
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path[0];
        var name = target.Name;

        var columns = new List<ColumnInfo>();
        await using (var cmd = Command(session,
            """
            SELECT c.column_name, c.data_type, c.is_nullable = 'YES', c.column_default,
                   COALESCE(pk.is_pk, false), c.is_identity = 'YES',
                   col_description(format('%I.%I', c.table_schema, c.table_name)::regclass, c.ordinal_position),
                   c.ordinal_position
              FROM information_schema.columns c
              LEFT JOIN (
                   SELECT kcu.column_name, true AS is_pk
                     FROM information_schema.table_constraints tc
                     JOIN information_schema.key_column_usage kcu
                       ON kcu.constraint_name = tc.constraint_name
                      AND kcu.table_schema = tc.table_schema
                    WHERE tc.constraint_type = 'PRIMARY KEY'
                      AND tc.table_schema = @s AND tc.table_name = @t
              ) pk ON pk.column_name = c.column_name
             WHERE c.table_schema = @s AND c.table_name = @t
             ORDER BY c.ordinal_position
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7)));
        }

        var indexes = new List<IndexInfo>();
        await using (var cmd = Command(session,
            """
            SELECT i.relname,
                   ARRAY(SELECT pg_get_indexdef(ix.indexrelid, k + 1, true)
                           FROM generate_subscripts(ix.indkey, 1) AS k ORDER BY k),
                   ix.indisunique, ix.indisprimary,
                   pg_get_expr(ix.indpred, ix.indrelid)
              FROM pg_index ix
              JOIN pg_class i ON i.oid = ix.indexrelid
              JOIN pg_class t ON t.oid = ix.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
             WHERE n.nspname = @s AND t.relname = @t
             ORDER BY i.relname
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                indexes.Add(new IndexInfo(
                    reader.GetString(0), reader.GetFieldValue<string[]>(1),
                    reader.GetBoolean(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        var foreignKeys = new List<ForeignKeyInfo>();
        await using (var cmd = Command(session,
            """
            SELECT con.conname,
                   ARRAY(SELECT att.attname FROM unnest(con.conkey) AS k
                           JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = k),
                   fn.nspname, ft.relname,
                   ARRAY(SELECT att.attname FROM unnest(con.confkey) AS k
                           JOIN pg_attribute att ON att.attrelid = con.confrelid AND att.attnum = k),
                   con.confdeltype, con.confupdtype
              FROM pg_constraint con
              JOIN pg_class t ON t.oid = con.conrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              JOIN pg_class ft ON ft.oid = con.confrelid
              JOIN pg_namespace fn ON fn.oid = ft.relnamespace
             WHERE con.contype = 'f' AND n.nspname = @s AND t.relname = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                foreignKeys.Add(new ForeignKeyInfo(
                    reader.GetString(0), reader.GetFieldValue<string[]>(1),
                    reader.GetString(2), reader.GetString(3), reader.GetFieldValue<string[]>(4),
                    Action(reader.GetChar(5)), Action(reader.GetChar(6))));
        }

        var triggers = new List<TriggerInfo>();
        await using (var cmd = Command(session,
            """
            SELECT trigger_name, action_timing, event_manipulation
              FROM information_schema.triggers
             WHERE event_object_schema = @s AND event_object_table = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                triggers.Add(new TriggerInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        long? rows = null;
        long? size = null;
        string? comment = null;
        await using (var cmd = Command(session,
            """
            SELECT c.reltuples::bigint, pg_total_relation_size(c.oid), obj_description(c.oid)
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = @s AND c.relname = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                rows = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                size = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                comment = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        return new ObjectDetail(target, columns, indexes, foreignKeys, triggers, rows, size, comment, null);

        static string Action(char code) => code switch
        {
            'a' => "NO ACTION", 'r' => "RESTRICT", 'c' => "CASCADE",
            'n' => "SET NULL", 'd' => "SET DEFAULT", _ => code.ToString(),
        };
    }

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        var prefix = mode == PlanMode.Actual
            ? "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            : "EXPLAIN (FORMAT JSON) ";

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = prefix + sql;
        var json = (string)(await cmd.ExecuteScalarAsync(ct))!;

        using var document = JsonDocument.Parse(json);
        return Convert(document.RootElement[0].GetProperty("Plan"));

        static PlanNode Convert(JsonElement element)
        {
            var children = element.TryGetProperty("Plans", out var plans)
                ? plans.EnumerateArray().Select(Convert).ToList()
                : [];

            var operation = element.GetProperty("Node Type").GetString()!;
            var estimatedRows = Number(element, "Plan Rows");
            var actualRows = Number(element, "Actual Rows");

            var warnings = new List<string>();
            if (operation == "Seq Scan" && estimatedRows > 1000) warnings.Add("sequential scan over many rows");
            if (actualRows is not null && estimatedRows is > 0 && actualRows > estimatedRows * 10)
                warnings.Add("row estimate is off by more than 10x; statistics may be stale");
            if (element.TryGetProperty("Sort Space Type", out var space) && space.GetString() == "Disk")
                warnings.Add("sort spilled to disk");

            return new PlanNode(
                operation,
                element.TryGetProperty("Relation Name", out var rel) ? rel.GetString() : null,
                Number(element, "Total Cost"), estimatedRows, actualRows,
                Number(element, "Actual Total Time"), children, warnings);
        }

        static double? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetDouble(out var d) ? d : null;
    }

    protected override (int? Line, int? Column) LocateError(DbException exception, string sql)
    {
        // Npgsql reports a 1-based character position in the statement; turn it into line/column.
        if (exception is not PostgresException { Position: > 0 } pg) return (null, null);

        var offset = Math.Min(pg.Position - 1, sql.Length - 1);
        var line = 1;
        var column = 1;
        for (var i = 0; i < offset; i++)
        {
            if (sql[i] == '\n') { line++; column = 1; }
            else column++;
        }
        return (line, column);
    }

    // --- helpers -----------------------------------------------------------

    private static SchemaNode Folder(SchemaNodeKind kind, string schema, string slug, string label) =>
        new(new SchemaNodeRef(kind, [schema, slug]), label, true);

    private static DbCommand Command(IDbSession session, string sql, string schema, string? table = null)
    {
        var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter("s", schema));
        if (table is not null) cmd.Parameters.Add(new NpgsqlParameter("t", table));
        return cmd;
    }

    private static async Task<IReadOnlyList<SchemaNode>> QueryNodesAsync(
        IDbSession session, CancellationToken ct, string sql, Func<string, SchemaNode> map)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) nodes.Add(map(reader.GetString(0)));
        return nodes;
    }

    private static async Task<IReadOnlyList<SchemaNode>> ListAsync(
        IDbSession session, CancellationToken ct, string schema, SchemaNodeKind kind, string sql)
    {
        await using var cmd = Command(session, sql, schema);
        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            nodes.Add(new SchemaNode(new SchemaNodeRef(kind, [schema, name]), name,
                HasChildren: kind is SchemaNodeKind.Table or SchemaNodeKind.View));
        }
        return nodes;
    }
}
```

- [ ] **Step 6: Run the contract suite**

Run: `dotnet test --filter PostgreSqlContract`
Expected: PASS. Docker must be running; the first run pulls `postgres:17-alpine`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: PostgreSQL driver"
```

---

### Task 5: MySQL / MariaDB driver

**Files:**
- Create: `src/WebDataStudio.Server/Drivers/MySql/MySqlDialect.cs`
- Create: `src/WebDataStudio.Server/Drivers/MySql/MySqlDriver.cs`
- Create: `tests/WebDataStudio.Server.Tests/Drivers/MySqlFixture.cs`
- Modify: `Directory.Packages.props` (add `MySqlConnector`, `Testcontainers.MySql`)

**Interfaces:**
- Consumes: `AdoDriverBase`, `DriverContractTests<T>`.
- Produces: `MySqlDriver`, `MySqlDialect`.

- [ ] **Step 1: Add the packages and write the fixture**

`tests/WebDataStudio.Server.Tests/Drivers/MySqlFixture.cs`:

```csharp
using MySqlConnector;
using Testcontainers.MySql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.MySql;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public sealed class MySqlFixture : IDriverFixture
{
    private readonly MySqlContainer _container = new MySqlBuilder()
        .WithImage("mysql:8.4").WithDatabase("shop").Build();

    public IDbDriver Driver { get; } = new MySqlDriver();
    public ConnectionSpec Spec => new("t", "test", "mysql", _container.GetConnectionString(),
        false, null, null, ConnectionSource.Stored);
    public string? Schema => "shop";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = new MySqlConnection(Spec.ConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, active TINYINT(1) NOT NULL);
            INSERT INTO people (name, active) VALUES ('ada',1),('linus',1),('grace',0);
            CREATE TABLE orders (id INT AUTO_INCREMENT PRIMARY KEY,
                                 person_id INT NOT NULL,
                                 total DECIMAL(10,2),
                                 CONSTRAINT fk_orders_person FOREIGN KEY (person_id) REFERENCES people(id));
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class MySqlContractTests(MySqlFixture fixture) : DriverContractTests<MySqlFixture>(fixture);
```

- [ ] **Step 2: Run the suite to verify it fails**

Run: `dotnet test --filter MySqlContract`
Expected: FAIL — `MySqlDriver` still throws `NotImplementedException`.

- [ ] **Step 3: Write the dialect**

`Drivers/MySql/MySqlDialect.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.MySql;

public sealed class MySqlDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "`" + name.Replace("`", "``") + "`";
    public override string ParameterPrefix => "@";
    public override string Paginate(string sql, int offset, int limit) => $"{sql} LIMIT {limit} OFFSET {offset}";
}
```

- [ ] **Step 4: Write the driver**

`Drivers/MySql/MySqlDriver.cs`:

```csharp
using System.Data.Common;
using MySqlConnector;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.MySql;

public sealed class MySqlDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } =
        new("mysql", "MySQL / MariaDB", 3306, "Server=localhost;Port=3306;Database=mysql;User ID=root;Password=");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiDatabase = true, Transactions = true, Ddl = true,
        EstimatedPlan = true, ActualPlan = true, StoredProcedures = true, Triggers = true,
        Views = true, ForeignKeys = true, Backup = true, Restore = true,
        UserManagement = true, SessionList = true, KillSession = true, ServerStats = true,
        SlowQueryLog = true, SystemCommands = true,
    };

    public override SqlDialect Dialect { get; } = new MySqlDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new MySqlConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct)
    {
        // In MySQL a schema IS a database, so the root lists databases as schema nodes.
        if (parent is null)
            return await QueryAsync(session, ct,
                """
                SELECT schema_name FROM information_schema.schemata
                 WHERE schema_name NOT IN ('mysql','information_schema','performance_schema','sys')
                 ORDER BY schema_name
                """,
                name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [name]), name, true));

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var s = parent.Name;
            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TableFolder, [s, "tables"]), "Tables", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ViewFolder, [s, "views"]), "Views", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ProcedureFolder, [s, "procedures"]), "Procedures", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.FunctionFolder, [s, "functions"]), "Functions", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TriggerFolder, [s, "triggers"]), "Triggers", true),
            ];
        }

        var schema = parent.Path[0];
        var (sql, kind) = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => (
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE' ORDER BY table_name",
                SchemaNodeKind.Table),
            SchemaNodeKind.ViewFolder => (
                "SELECT table_name FROM information_schema.views WHERE table_schema = @s ORDER BY table_name",
                SchemaNodeKind.View),
            SchemaNodeKind.ProcedureFolder => (
                "SELECT routine_name FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'PROCEDURE' ORDER BY routine_name",
                SchemaNodeKind.Procedure),
            SchemaNodeKind.FunctionFolder => (
                "SELECT routine_name FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'FUNCTION' ORDER BY routine_name",
                SchemaNodeKind.Function),
            SchemaNodeKind.TriggerFolder => (
                "SELECT trigger_name FROM information_schema.triggers WHERE trigger_schema = @s ORDER BY trigger_name",
                SchemaNodeKind.Trigger),
            _ => (null, SchemaNodeKind.Table),
        };
        if (sql is null) return [];

        return await QueryAsync(session, ct, sql,
            name => new SchemaNode(new SchemaNodeRef(kind, [schema, name]), name,
                HasChildren: kind is SchemaNodeKind.Table or SchemaNodeKind.View),
            schema);
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path[0];
        var name = target.Name;

        var columns = new List<ColumnInfo>();
        await using (var cmd = Command(session,
            """
            SELECT column_name, column_type, is_nullable = 'YES', column_default,
                   column_key = 'PRI', extra LIKE '%auto_increment%', column_comment, ordinal_position
              FROM information_schema.columns
             WHERE table_schema = @s AND table_name = @t
             ORDER BY ordinal_position
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7)));
        }

        var indexes = new Dictionary<string, IndexInfo>();
        await using (var cmd = Command(session,
            """
            SELECT index_name, column_name, non_unique = 0
              FROM information_schema.statistics
             WHERE table_schema = @s AND table_name = @t
             ORDER BY index_name, seq_in_index
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var indexName = reader.GetString(0);
                var column = reader.GetString(1);
                if (!indexes.TryGetValue(indexName, out var existing))
                    existing = new IndexInfo(indexName, [], reader.GetBoolean(2), indexName == "PRIMARY", null);
                indexes[indexName] = existing with { Columns = existing.Columns.Append(column).ToList() };
            }
        }

        var foreignKeys = new Dictionary<string, ForeignKeyInfo>();
        await using (var cmd = Command(session,
            """
            SELECT k.constraint_name, k.column_name, k.referenced_table_schema,
                   k.referenced_table_name, k.referenced_column_name,
                   r.delete_rule, r.update_rule
              FROM information_schema.key_column_usage k
              JOIN information_schema.referential_constraints r
                ON r.constraint_schema = k.constraint_schema AND r.constraint_name = k.constraint_name
             WHERE k.table_schema = @s AND k.table_name = @t AND k.referenced_table_name IS NOT NULL
             ORDER BY k.constraint_name, k.ordinal_position
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                if (!foreignKeys.TryGetValue(key, out var existing))
                    existing = new ForeignKeyInfo(key, [], reader.GetString(2), reader.GetString(3), [],
                        reader.GetString(5), reader.GetString(6));
                foreignKeys[key] = existing with
                {
                    Columns = existing.Columns.Append(reader.GetString(1)).ToList(),
                    ReferencedColumns = existing.ReferencedColumns.Append(reader.GetString(4)).ToList(),
                };
            }
        }

        var triggers = new List<TriggerInfo>();
        await using (var cmd = Command(session,
            """
            SELECT trigger_name, action_timing, event_manipulation
              FROM information_schema.triggers
             WHERE event_object_schema = @s AND event_object_table = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                triggers.Add(new TriggerInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        long? rows = null;
        long? size = null;
        string? comment = null;
        await using (var cmd = Command(session,
            """
            SELECT table_rows, data_length + index_length, table_comment
              FROM information_schema.tables WHERE table_schema = @s AND table_name = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                rows = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                size = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                comment = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        return new ObjectDetail(target, columns, indexes.Values.ToList(), foreignKeys.Values.ToList(),
            triggers, rows, size, comment, null);
    }

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
    {
        // MySQL 8 returns a tree; ANALYZE adds measured timings.
        var prefix = mode == PlanMode.Actual ? "EXPLAIN ANALYZE " : "EXPLAIN FORMAT=TREE ";
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = prefix + sql;

        var text = (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "";
        var children = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => new PlanNode(l.Trim().TrimStart('-', '>', ' '), null, null, null, null, null, [],
                l.Contains("Table scan", StringComparison.OrdinalIgnoreCase) ? ["full table scan"] : []))
            .ToList();

        return new PlanNode("EXPLAIN", null, null, null, null, null, children, []);
    }

    // --- helpers -----------------------------------------------------------

    private static DbCommand Command(IDbSession session, string sql, string schema, string? table = null)
    {
        var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new MySqlParameter("@s", schema));
        if (table is not null) cmd.Parameters.Add(new MySqlParameter("@t", table));
        return cmd;
    }

    private static async Task<IReadOnlyList<SchemaNode>> QueryAsync(
        IDbSession session, CancellationToken ct, string sql, Func<string, SchemaNode> map, string? schema = null)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        if (schema is not null) cmd.Parameters.Add(new MySqlParameter("@s", schema));

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) nodes.Add(map(reader.GetString(0)));
        return nodes;
    }
}
```

- [ ] **Step 5: Run the contract suite**

Run: `dotnet test --filter MySqlContract`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: MySQL and MariaDB driver"
```

---

### Task 6: SQL Server driver

**Files:**
- Create: `src/WebDataStudio.Server/Drivers/SqlServer/SqlServerDialect.cs`
- Create: `src/WebDataStudio.Server/Drivers/SqlServer/SqlServerDriver.cs`
- Create: `tests/WebDataStudio.Server.Tests/Drivers/SqlServerFixture.cs`
- Modify: `Directory.Packages.props` (add `Microsoft.Data.SqlClient`, `Testcontainers.MsSql`)

**Interfaces:**
- Consumes: `AdoDriverBase`, `DriverContractTests<T>`.
- Produces: `SqlServerDriver`, `SqlServerDialect` (the splitter tests in Task 2 already reference `SqlServerDialect` and its `UsesGoBatchSeparator => true`).

- [ ] **Step 1: Add the packages and write the fixture**

`tests/WebDataStudio.Server.Tests/Drivers/SqlServerFixture.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.SqlServer;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Tests.Drivers;

public sealed class SqlServerFixture : IDriverFixture
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public IDbDriver Driver { get; } = new SqlServerDriver();
    public ConnectionSpec Spec => new("t", "test", "sqlserver", _container.GetConnectionString(),
        false, null, null, ConnectionSource.Stored);
    public string? Schema => "dbo";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = new SqlConnection(Spec.ConnectionString);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INT IDENTITY PRIMARY KEY, name NVARCHAR(100) NOT NULL, active BIT NOT NULL);
            INSERT INTO people (name, active) VALUES ('ada',1),('linus',1),('grace',0);
            CREATE TABLE orders (id INT IDENTITY PRIMARY KEY,
                                 person_id INT NOT NULL CONSTRAINT fk_orders_person REFERENCES people(id),
                                 total DECIMAL(10,2));
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public class SqlServerContractTests(SqlServerFixture fixture) : DriverContractTests<SqlServerFixture>(fixture);
```

- [ ] **Step 2: Run the suite to verify it fails**

Run: `dotnet test --filter SqlServerContract`
Expected: FAIL — `SqlServerDriver` still throws `NotImplementedException`.

- [ ] **Step 3: Write the dialect**

`Drivers/SqlServer/SqlServerDialect.cs`:

```csharp
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.SqlServer;

public sealed class SqlServerDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";
    public override string ParameterPrefix => "@";
    public override bool UsesGoBatchSeparator => true;

    // SQL Server requires an ORDER BY for OFFSET/FETCH; a stable no-op ordering keeps it legal.
    public override string Paginate(string sql, int offset, int limit) =>
        sql.Contains("order by", StringComparison.OrdinalIgnoreCase)
            ? $"{sql} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"
            : $"{sql} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
}
```

- [ ] **Step 4: Write the driver**

`Drivers/SqlServer/SqlServerDriver.cs`:

```csharp
using System.Data.Common;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.SqlServer;

public sealed class SqlServerDriver : AdoDriverBase
{
    public override DriverInfo Info { get; } =
        new("sqlserver", "SQL Server", 1433, "Server=localhost,1433;Database=master;User Id=sa;Password=;TrustServerCertificate=True");

    public override DriverCapabilities Caps { get; } = new()
    {
        Sql = true, MultiSchema = true, MultiDatabase = true, Transactions = true, Ddl = true,
        EstimatedPlan = true, ActualPlan = true, StoredProcedures = true, Triggers = true,
        Views = true, Sequences = true, ForeignKeys = true, PartialIndexes = true,
        IncludeColumns = true, Backup = true, Restore = true, UserManagement = true,
        SessionList = true, KillSession = true, ServerStats = true, SlowQueryLog = true,
        SystemCommands = true,
    };

    public override SqlDialect Dialect { get; } = new SqlServerDialect();

    public override async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var connection = new SqlConnection(spec.ConnectionString);
        await connection.OpenAsync(ct);
        return new AdoSession(spec, connection);
    }

    public override async Task<IReadOnlyList<SchemaNode>> IntrospectAsync(
        IDbSession session, SchemaNodeRef? parent, CancellationToken ct)
    {
        if (parent is null)
            return await QueryAsync(session, ct,
                "SELECT name FROM sys.schemas WHERE name NOT IN ('sys','INFORMATION_SCHEMA') AND name NOT LIKE 'db_%' ORDER BY name",
                name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Schema, [name]), name, true));

        if (parent.Kind == SchemaNodeKind.Schema)
        {
            var s = parent.Name;
            return
            [
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.TableFolder, [s, "tables"]), "Tables", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ViewFolder, [s, "views"]), "Views", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.ProcedureFolder, [s, "procedures"]), "Procedures", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.FunctionFolder, [s, "functions"]), "Functions", true),
                new SchemaNode(new SchemaNodeRef(SchemaNodeKind.SequenceFolder, [s, "sequences"]), "Sequences", true),
            ];
        }

        var schema = parent.Path[0];
        var (sql, kind) = parent.Kind switch
        {
            SchemaNodeKind.TableFolder => (
                "SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @s ORDER BY t.name",
                SchemaNodeKind.Table),
            SchemaNodeKind.ViewFolder => (
                "SELECT v.name FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id WHERE s.name = @s ORDER BY v.name",
                SchemaNodeKind.View),
            SchemaNodeKind.ProcedureFolder => (
                "SELECT p.name FROM sys.procedures p JOIN sys.schemas s ON s.schema_id = p.schema_id WHERE s.name = @s ORDER BY p.name",
                SchemaNodeKind.Procedure),
            SchemaNodeKind.FunctionFolder => (
                """
                SELECT o.name FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
                 WHERE s.name = @s AND o.type IN ('FN','IF','TF') ORDER BY o.name
                """,
                SchemaNodeKind.Function),
            SchemaNodeKind.SequenceFolder => (
                "SELECT q.name FROM sys.sequences q JOIN sys.schemas s ON s.schema_id = q.schema_id WHERE s.name = @s ORDER BY q.name",
                SchemaNodeKind.Sequence),
            _ => (null, SchemaNodeKind.Table),
        };
        if (sql is null) return [];

        return await QueryAsync(session, ct, sql,
            name => new SchemaNode(new SchemaNodeRef(kind, [schema, name]), name,
                HasChildren: kind is SchemaNodeKind.Table or SchemaNodeKind.View),
            schema);
    }

    public override async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var schema = target.Path[0];
        var name = target.Name;

        var columns = new List<ColumnInfo>();
        await using (var cmd = Command(session,
            """
            SELECT c.name,
                   t.name + CASE WHEN t.name IN ('varchar','nvarchar','char','nchar')
                                 THEN '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length AS varchar)) + ')'
                                 ELSE '' END,
                   c.is_nullable,
                   dc.definition,
                   IIF(pk.column_id IS NULL, 0, 1),
                   c.is_identity,
                   ep.value,
                   c.column_id
              FROM sys.columns c
              JOIN sys.objects o ON o.object_id = c.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              JOIN sys.types t ON t.user_type_id = c.user_type_id
              LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
              LEFT JOIN (
                    SELECT ic.object_id, ic.column_id
                      FROM sys.index_columns ic
                      JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                     WHERE i.is_primary_key = 1
              ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
              LEFT JOIN sys.extended_properties ep
                     ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
             WHERE s.name = @s AND o.name = @t
             ORDER BY c.column_id
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new ColumnInfo(
                    reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4) == 1, reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetValue(6).ToString(),
                    reader.GetInt32(7)));
        }

        var indexes = new Dictionary<string, IndexInfo>();
        await using (var cmd = Command(session,
            """
            SELECT i.name, c.name, i.is_unique, i.is_primary_key, i.filter_definition
              FROM sys.indexes i
              JOIN sys.objects o ON o.object_id = i.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE s.name = @s AND o.name = @t AND i.name IS NOT NULL
             ORDER BY i.name, ic.key_ordinal
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var indexName = reader.GetString(0);
                if (!indexes.TryGetValue(indexName, out var existing))
                    existing = new IndexInfo(indexName, [], reader.GetBoolean(2), reader.GetBoolean(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4));
                indexes[indexName] = existing with { Columns = existing.Columns.Append(reader.GetString(1)).ToList() };
            }
        }

        var foreignKeys = new Dictionary<string, ForeignKeyInfo>();
        await using (var cmd = Command(session,
            """
            SELECT fk.name, pc.name, rs.name, rt.name, rc.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc
              FROM sys.foreign_keys fk
              JOIN sys.objects t ON t.object_id = fk.parent_object_id
              JOIN sys.schemas s ON s.schema_id = t.schema_id
              JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
              JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
              JOIN sys.objects rt ON rt.object_id = fk.referenced_object_id
              JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
              JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
             WHERE s.name = @s AND t.name = @t
             ORDER BY fk.name, fkc.constraint_column_id
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                if (!foreignKeys.TryGetValue(key, out var existing))
                    existing = new ForeignKeyInfo(key, [], reader.GetString(2), reader.GetString(3), [],
                        reader.GetString(5), reader.GetString(6));
                foreignKeys[key] = existing with
                {
                    Columns = existing.Columns.Append(reader.GetString(1)).ToList(),
                    ReferencedColumns = existing.ReferencedColumns.Append(reader.GetString(4)).ToList(),
                };
            }
        }

        var triggers = new List<TriggerInfo>();
        await using (var cmd = Command(session,
            """
            SELECT tr.name,
                   IIF(OBJECTPROPERTY(tr.object_id,'ExecIsInsteadOfTrigger') = 1, 'INSTEAD OF', 'AFTER'),
                   STUFF(
                     IIF(OBJECTPROPERTY(tr.object_id,'ExecIsInsertTrigger') = 1, ',INSERT', '') +
                     IIF(OBJECTPROPERTY(tr.object_id,'ExecIsUpdateTrigger') = 1, ',UPDATE', '') +
                     IIF(OBJECTPROPERTY(tr.object_id,'ExecIsDeleteTrigger') = 1, ',DELETE', ''), 1, 1, '')
              FROM sys.triggers tr
              JOIN sys.objects o ON o.object_id = tr.parent_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
             WHERE s.name = @s AND o.name = @t
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                triggers.Add(new TriggerInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        long? rows = null;
        long? size = null;
        await using (var cmd = Command(session,
            """
            SELECT SUM(p.rows), SUM(a.total_pages) * 8192
              FROM sys.partitions p
              JOIN sys.allocation_units a ON a.container_id = p.partition_id
              JOIN sys.objects o ON o.object_id = p.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
             WHERE s.name = @s AND o.name = @t AND p.index_id IN (0,1)
            """, schema, name))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                rows = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                size = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }

        return new ObjectDetail(target, columns, indexes.Values.ToList(), foreignKeys.Values.ToList(),
            triggers, rows, size, null, null);
    }

    public override async Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct)
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

        var ns = XNamespace.Get("http://schemas.microsoft.com/sqlserver/2004/07/showplan");
        var root = XDocument.Parse(xml).Descendants(ns + "RelOp").FirstOrDefault();
        return root is null
            ? new PlanNode("Plan", null, null, null, null, null, [], [])
            : Convert(root, ns);

        static PlanNode Convert(XElement element, XNamespace ns)
        {
            var children = element.Descendants(ns + "RelOp")
                .Where(e => e.Parent?.Parent == element)
                .Select(e => Convert(e, ns)).ToList();

            var operation = (string?)element.Attribute("PhysicalOp") ?? "RelOp";
            var estimatedRows = (double?)element.Attribute("EstimateRows");
            var warnings = new List<string>();
            if (operation.Contains("Scan", StringComparison.OrdinalIgnoreCase) && estimatedRows > 1000)
                warnings.Add("scan over many rows");
            if (element.Descendants(ns + "Warnings").Any()) warnings.Add("the server reported a plan warning");

            return new PlanNode(operation, (string?)element.Attribute("LogicalOp"),
                (double?)element.Attribute("EstimatedTotalSubtreeCost"), estimatedRows,
                (double?)element.Attribute("ActualRows"), null, children, warnings);
        }
    }

    private static async Task<string?> ReadPlanXmlAsync(DbDataReader reader, CancellationToken ct)
    {
        do
        {
            if (reader.FieldCount == 1 &&
                reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase) &&
                await reader.ReadAsync(ct))
                return reader.GetString(0);

            while (await reader.ReadAsync(ct)) { /* skip the data rows of the query itself */ }
        }
        while (await reader.NextResultAsync(ct));

        return null;
    }

    protected override (int? Line, int? Column) LocateError(DbException exception, string sql) =>
        exception is SqlException { LineNumber: > 0 } sqlException ? (sqlException.LineNumber, null) : (null, null);

    // --- helpers -----------------------------------------------------------

    private static DbCommand Command(IDbSession session, string sql, string schema, string? table = null)
    {
        var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@s", schema));
        if (table is not null) cmd.Parameters.Add(new SqlParameter("@t", table));
        return cmd;
    }

    private static async Task<IReadOnlyList<SchemaNode>> QueryAsync(
        IDbSession session, CancellationToken ct, string sql, Func<string, SchemaNode> map, string? schema = null)
    {
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        if (schema is not null) cmd.Parameters.Add(new SqlParameter("@s", schema));

        var nodes = new List<SchemaNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) nodes.Add(map(reader.GetString(0)));
        return nodes;
    }
}
```

- [ ] **Step 5: Run the contract suite**

Run: `dotnet test --filter SqlServerContract`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: SQL Server driver"
```

---

### Task 7: Capability honesty test

**Files:**
- Create: `tests/WebDataStudio.Server.Tests/Drivers/CapabilityHonestyTests.cs`

**Interfaces:**
- Consumes: `DriverRegistry`, `IDbDriver`.
- Produces: nothing — a guard test that every later driver must also pass.

- [ ] **Step 1: Write the test**

```csharp
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Drivers;

/// A capability declared false must fail loudly and predictably, not obscurely. This runs without
/// a live database: it only needs the driver instances.
public class CapabilityHonestyTests
{
    public static TheoryData<string> Engines()
    {
        var data = new TheoryData<string>();
        foreach (var driver in new DriverRegistry().All()) data.Add(driver.Info.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Explain_throws_NotSupportedException_when_the_capability_is_false(string engine)
    {
        var driver = new DriverRegistry().Get(engine);
        if (driver.Caps.EstimatedPlan) return;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            driver.ExplainAsync(null!, "SELECT 1", PlanMode.Estimated, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Driver_metadata_is_complete(string engine)
    {
        var driver = new DriverRegistry().Get(engine);
        Assert.False(string.IsNullOrWhiteSpace(driver.Info.Label));
        Assert.False(string.IsNullOrWhiteSpace(driver.Info.ConnectionStringTemplate));
        Assert.NotNull(driver.Dialect);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Sql_engines_expose_a_working_dialect(string engine)
    {
        var driver = new DriverRegistry().Get(engine);
        if (!driver.Caps.Sql) return;

        Assert.NotEqual("x", driver.Dialect.QuoteIdentifier("x"));
        Assert.NotEmpty(driver.Dialect.Paginate("SELECT 1", 0, 10));
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test --filter CapabilityHonesty`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test: capability honesty guard for every driver"
```

---

### Task 8: Schema and query endpoints

**Files:**
- Create: `src/WebDataStudio.Server/Services/SessionFactory.cs`
- Create: `src/WebDataStudio.Server/Services/QueryRunner.cs`
- Create: `src/WebDataStudio.Server/Endpoints/SchemaEndpoints.cs`
- Create: `src/WebDataStudio.Server/Endpoints/QueryEndpoints.cs`
- Modify: `src/WebDataStudio.Server/Program.cs`
- Modify: `src/WebDataStudio.Server/Endpoints/ConnectionEndpoints.cs` (real `POST /test`)
- Create: `tests/WebDataStudio.Server.Tests/QueryEndpointTests.cs`

**Interfaces:**
- Consumes: `DriverRegistry`, `ConnectionRegistry`, `IDbDriver`.
- Produces:
  - `SessionFactory.OpenAsync(string connectionId, CancellationToken) -> Task<(IDbDriver, IDbSession)>`
  - `QueryRunner` with `string Start()` (returns a run id), `CancellationToken TokenFor(string runId)`, `bool Cancel(string runId)`, `void Finish(string runId)`.
  - `GET /api/schema/{conn}?parent=`, `GET /api/schema/{conn}/object/{ref}`, `GET /api/drivers`,
    `POST /api/query/execute` (NDJSON), `POST /api/query/{runId}/cancel`, `POST /api/query/plan`.

- [ ] **Step 1: Write the failing tests**

`tests/WebDataStudio.Server.Tests/QueryEndpointTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

public class QueryEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-query").FullName;
    private string _dataDb = "";

    public async ValueTask InitializeAsync()
    {
        _dataDb = Path.Combine(_dir, "demo.db");
        await using var db = new SqliteConnection($"Data Source={_dataDb}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people VALUES (1,'ada'),(2,'linus');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_DEMO"] = $"sqlite://{_dataDb.Replace('\\', '/')}",
            })));

    private static async Task<string> ConnectionIdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/connections"));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Lists_drivers_with_capabilities()
    {
        using var factory = Factory();
        var raw = await factory.CreateClient().GetStringAsync("/api/drivers");
        Assert.Contains("sqlite", raw);
        Assert.Contains("estimatedPlan", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_the_schema_root()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var raw = await client.GetStringAsync($"/api/schema/{await ConnectionIdAsync(client)}");
        Assert.Contains("Tables", raw);
    }

    [Fact]
    public async Task Executes_a_query_and_streams_ndjson()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = await ConnectionIdAsync(client),
            sql = "SELECT id, name FROM people ORDER BY id",
            maxRows = 100,
        });
        response.EnsureSuccessStatusCode();

        var lines = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(lines, l => l.Contains("\"type\":\"columns\""));
        Assert.Contains(lines, l => l.Contains("ada"));
        Assert.Contains(lines, l => l.Contains("\"type\":\"end\""));
    }

    [Fact]
    public async Task A_syntax_error_arrives_as_an_error_line_not_a_500()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = await ConnectionIdAsync(client),
            sql = "SELECT FROM WHERE",
            maxRows = 100,
        });

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"type\":\"error\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rejects_an_unknown_connection()
    {
        using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/query/execute", new
        {
            connectionId = "nope", sql = "SELECT 1", maxRows = 10,
        });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Test_connection_probes_the_database_for_real()
    {
        using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/connections/test", new
        {
            name = "probe", engine = "sqlite",
            connectionString = $"Data Source={Path.Combine(_dir, "missing-dir", "x.db")}",
            readOnly = false,
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter QueryEndpoint`
Expected: 404 responses — the endpoints do not exist.

- [ ] **Step 3: Write `SessionFactory` and `QueryRunner`**

`src/WebDataStudio.Server/Services/SessionFactory.cs`:

```csharp
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

public sealed class UnknownConnectionException(string id)
    : Exception($"no connection with id '{id}'");

public sealed class SessionFactory(ConnectionRegistry registry, DriverRegistry drivers)
{
    public async Task<(IDbDriver Driver, IDbSession Session)> OpenAsync(string connectionId, CancellationToken ct)
    {
        var spec = registry.Find(connectionId) ?? throw new UnknownConnectionException(connectionId);
        var driver = drivers.Get(spec.Engine);
        return (driver, await driver.OpenAsync(spec, ct));
    }
}
```

`src/WebDataStudio.Server/Services/QueryRunner.cs`:

```csharp
using System.Collections.Concurrent;

namespace WebDataStudio.Server.Services;

/// Tracks in-flight query runs so a second request can cancel one.
public sealed class QueryRunner
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();

    public (string RunId, CancellationTokenSource Source) Start(CancellationToken requestAborted)
    {
        var runId = Guid.NewGuid().ToString("n");
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        _runs[runId] = source;
        return (runId, source);
    }

    public bool Cancel(string runId)
    {
        if (!_runs.TryGetValue(runId, out var source)) return false;
        source.Cancel();
        return true;
    }

    public void Finish(string runId)
    {
        if (_runs.TryRemove(runId, out var source)) source.Dispose();
    }
}
```

- [ ] **Step 4: Write the schema endpoints**

`src/WebDataStudio.Server/Endpoints/SchemaEndpoints.cs`:

```csharp
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class SchemaEndpoints
{
    public static void MapSchemaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/drivers", (DriverRegistry drivers) =>
            Results.Ok(drivers.All().Select(d => new { d.Info, d.Caps })));

        app.MapGet("/api/schema/{conn}", async (string conn, string? parent,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var parentRef = string.IsNullOrEmpty(parent) ? null : SchemaNodeRef.Parse(parent);
                    var nodes = await driver.IntrospectAsync(session, parentRef, ct);
                    return Results.Ok(nodes.Select(n => new
                    {
                        @ref = n.Ref.ToString(),
                        kind = n.Ref.Kind.ToString(),
                        label = n.Label,
                        hasChildren = n.HasChildren,
                        detail = n.Detail,
                    }));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        app.MapGet("/api/schema/{conn}/object/{objectRef}", async (string conn, string objectRef,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await driver.DescribeAsync(session, SchemaNodeRef.Parse(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }
}
```

- [ ] **Step 5: Write the query endpoints**

`src/WebDataStudio.Server/Endpoints/QueryEndpoints.cs`:

```csharp
using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

public static class QueryEndpoints
{
    public record ExecuteRequest(string ConnectionId, string Sql, int? MaxRows, int? TimeoutSeconds,
        string? Schema, Dictionary<string, string?>? Parameters);

    public record PlanRequest(string ConnectionId, string Sql, string Mode);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapQueryEndpoints(this WebApplication app)
    {
        var defaultMaxRows = int.TryParse(app.Configuration["WDS_MAX_ROWS"], out var m) ? m : 1000;
        var defaultTimeout = int.TryParse(app.Configuration["WDS_QUERY_TIMEOUT_SECONDS"], out var t) ? t : 300;

        app.MapPost("/api/query/execute", async (ExecuteRequest body, HttpContext ctx,
            SessionFactory factory, QueryRunner runner) =>
        {
            IDbDriver driver;
            IDbSession session;
            try
            {
                (driver, session) = await factory.OpenAsync(body.ConnectionId, ctx.RequestAborted);
            }
            catch (UnknownConnectionException e)
            {
                return Results.NotFound(new { message = e.Message });
            }
            catch (Exception e)
            {
                return Results.Json(new { message = e.Message }, statusCode: 502);
            }

            var (runId, source) = runner.Start(ctx.RequestAborted);
            ctx.Response.Headers["X-Run-Id"] = runId;
            ctx.Response.ContentType = "application/x-ndjson";

            var request = new ScriptRequest(body.Sql, body.MaxRows ?? defaultMaxRows,
                body.TimeoutSeconds ?? defaultTimeout, body.Schema, body.Parameters);

            await using (session)
            {
                try
                {
                    await foreach (var chunk in driver.ExecuteAsync(session, request, source.Token))
                    {
                        await JsonSerializer.SerializeAsync(ctx.Response.Body, Wire(chunk), Json, source.Token);
                        await ctx.Response.Body.WriteAsync("\n"u8.ToArray(), source.Token);
                        await ctx.Response.Body.FlushAsync(source.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // A cancelled run is a normal outcome; tell the client so it can mark the tab.
                    await WriteAsync(ctx, new { type = "cancelled" });
                }
                finally
                {
                    runner.Finish(runId);
                }
            }

            return Results.Empty;
        });

        app.MapPost("/api/query/{runId}/cancel", (string runId, QueryRunner runner) =>
            runner.Cancel(runId) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/query/plan", async (PlanRequest body, SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(body.ConnectionId, ct);
                await using (session)
                {
                    var mode = body.Mode.Equals("actual", StringComparison.OrdinalIgnoreCase)
                        ? PlanMode.Actual : PlanMode.Estimated;
                    return Results.Ok(await driver.ExplainAsync(session, body.Sql, mode, ct));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (NotSupportedException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    private static async Task WriteAsync(HttpContext ctx, object payload)
    {
        await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, Json);
        await ctx.Response.Body.WriteAsync("\n"u8.ToArray());
        await ctx.Response.Body.FlushAsync();
    }

    /// The wire shape from spec section 5.3. Kept separate from ResultChunk so the record
    /// hierarchy can change without breaking the client contract.
    private static object Wire(ResultChunk chunk) => chunk switch
    {
        ResultChunk.Columns c => new { type = "columns", statement = c.Statement, columns = c.Items },
        ResultChunk.Rows r => new { type = "rows", statement = r.Statement, rows = r.Items },
        ResultChunk.Progress p => new { type = "progress", statement = p.Statement, rowsRead = p.RowsRead, elapsedMs = p.ElapsedMs },
        ResultChunk.Message m => new { type = "message", statement = m.Statement, severity = m.Severity, text = m.Text },
        ResultChunk.End e => new { type = "end", statement = e.Statement, rowsAffected = e.RowsAffected, elapsedMs = e.ElapsedMs, truncated = e.Truncated },
        ResultChunk.Error x => new { type = "error", statement = x.Statement, text = x.Text, code = x.Code, line = x.Line, column = x.Column },
        _ => new { type = "unknown" },
    };
}
```

- [ ] **Step 6: Make `POST /api/connections/test` probe for real**

Replace the placeholder body in `ConnectionEndpoints`:

```csharp
api.MapPost("/test", async (ConnectionRequest body, DriverRegistry drivers, CancellationToken ct) =>
{
    if (Validate(body) is { } error) return error;
    try
    {
        var driver = drivers.Get(body.Engine);
        var spec = new ConnectionSpec("probe", body.Name, body.Engine, body.ConnectionString,
            true, null, null, ConnectionSource.Stored);
        await using var session = await driver.OpenAsync(spec, ct);
        return Results.Ok(new { ok = true, message = $"connected to {driver.Info.Label}" });
    }
    catch (Exception e)
    {
        // A failed probe is information, not a server fault: 200 with ok=false keeps the form simple.
        return Results.Ok(new { ok = false, message = e.Message });
    }
});
```

- [ ] **Step 7: Register the services in `Program.cs`**

```csharp
builder.Services.AddSingleton<DriverRegistry>();
builder.Services.AddSingleton<SessionFactory>();
builder.Services.AddSingleton<QueryRunner>();
```

and after `app.MapConnectionEndpoints();`:

```csharp
app.MapSchemaEndpoints();
app.MapQueryEndpoints();
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test --filter QueryEndpoint`
Expected: PASS, 6 tests.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: schema and streaming query endpoints"
```

---

### Task 9: Object explorer UI

**Files:**
- Create: `web/src/explorer/ExplorerTree.tsx`
- Create: `web/src/explorer/ObjectDetailPanel.tsx`
- Create: `web/src/explorer/nodeIcons.tsx`
- Create: `web/src/dock/DockShell.tsx`
- Modify: `web/src/api.ts` (schema and query calls)
- Modify: `web/src/App.tsx`
- Create: `web/src/api.schema.test.ts`

**Interfaces:**
- Consumes: `GET /api/schema/{conn}`, `GET /api/schema/{conn}/object/{ref}`, `GET /api/drivers`.
- Produces:
  - `api.ts` additions: `listSchema(conn, parent?)`, `describeObject(conn, ref)`, `listDrivers()`, and the types `SchemaNodeDto`, `ObjectDetailDto`, `DriverDto`.
  - `<DockShell>` — the dockview host with the fixed explorer on the left and a dockview area on the right. P2 adds query panels into it.

- [ ] **Step 1: Write the failing test**

`web/src/api.schema.test.ts`:

```ts
import { describe, expect, it, vi } from "vitest";
import { listSchema } from "./api";

describe("listSchema", () => {
  it("requests the root level without a parent parameter", async () => {
    const fetchMock = vi.fn(async () => new Response("[]", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    await listSchema("c1");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/schema/c1");
  });

  it("passes the parent reference through", async () => {
    const fetchMock = vi.fn(async () => new Response("[]", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    await listSchema("c1", "Schema:public");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/schema/c1?parent=Schema%3Apublic");
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd web && npx vitest run api.schema`
Expected: FAIL — `listSchema` is not exported.

- [ ] **Step 3: Extend `api.ts`**

```ts
export interface SchemaNodeDto {
  ref: string; kind: string; label: string; hasChildren: boolean; detail: string | null;
}
export interface ColumnDto {
  name: string; dataType: string; nullable: boolean; default: string | null;
  isPrimaryKey: boolean; isIdentity: boolean; comment: string | null; position: number;
}
export interface IndexDto { name: string; columns: string[]; unique: boolean; primary: boolean; filter: string | null }
export interface ForeignKeyDto {
  name: string; columns: string[]; referencedSchema: string; referencedTable: string;
  referencedColumns: string[]; onDelete: string; onUpdate: string;
}
export interface ObjectDetailDto {
  columns: ColumnDto[]; indexes: IndexDto[]; foreignKeys: ForeignKeyDto[];
  triggers: { name: string; timing: string; event: string }[];
  rowCount: number | null; sizeBytes: number | null; comment: string | null; ddl: string | null;
}
export interface DriverDto {
  info: { id: string; label: string; defaultPort: number; connectionStringTemplate: string };
  caps: Record<string, boolean>;
}

export const listDrivers = (): Promise<DriverDto[]> => fetch(`${base}/drivers`).then(r => ok<DriverDto[]>(r));

export const listSchema = (conn: string, parent?: string): Promise<SchemaNodeDto[]> =>
  fetch(parent ? `${base}/schema/${conn}?parent=${encodeURIComponent(parent)}` : `${base}/schema/${conn}`)
    .then(r => ok<SchemaNodeDto[]>(r));

export const describeObject = (conn: string, ref: string): Promise<ObjectDetailDto> =>
  fetch(`${base}/schema/${conn}/object/${encodeURIComponent(ref)}`).then(r => ok<ObjectDetailDto>(r));
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd web && npx vitest run api.schema`
Expected: PASS, 2 tests.

- [ ] **Step 5: Write the icon map**

`web/src/explorer/nodeIcons.tsx`:

```tsx
import {
  IconDatabase, IconFolder, IconTable, IconEye, IconFunction, IconBolt,
  IconListNumbers, IconBinaryTree,
} from "@tabler/icons-react";

export function nodeIcon(kind: string) {
  const size = 15;
  switch (kind) {
    case "Database": case "Schema": return <IconDatabase size={size} />;
    case "Table": return <IconTable size={size} />;
    case "View": case "MaterializedView": return <IconEye size={size} />;
    case "Function": case "Procedure": return <IconFunction size={size} />;
    case "Trigger": return <IconBolt size={size} />;
    case "Sequence": return <IconListNumbers size={size} />;
    case "Index": return <IconBinaryTree size={size} />;
    default: return <IconFolder size={size} />;
  }
}
```

- [ ] **Step 6: Write the tree**

`web/src/explorer/ExplorerTree.tsx`:

```tsx
import { useEffect, useState } from "react";
import { ActionIcon, Badge, Group, Loader, Text, TextInput, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconRefresh, IconSearch } from "@tabler/icons-react";
import { listConnections, listSchema, type Connection, type SchemaNodeDto } from "../api";
import { nodeIcon } from "./nodeIcons";

export interface ExplorerSelection { connectionId: string; node: SchemaNodeDto }

// One lazily loaded level. Children are fetched on first expand and cached until a manual refresh.
function TreeLevel({ conn, parent, depth, filter, onSelect }: {
  conn: string; parent?: string; depth: number; filter: string;
  onSelect: (s: ExplorerSelection) => void;
}) {
  const [nodes, setNodes] = useState<SchemaNodeDto[] | null>(null);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    listSchema(conn, parent)
      .then(n => { if (!cancelled) setNodes(n); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [conn, parent]);

  if (error) return <Text c="red" size="xs" pl={depth * 12 + 8}>{error}</Text>;
  if (!nodes) return <Loader size="xs" ml={depth * 12 + 8} my={4} />;

  const visible = filter
    ? nodes.filter(n => n.label.toLowerCase().includes(filter.toLowerCase()))
    : nodes;

  return (
    <>
      {visible.map(node => (
        <div key={node.ref}>
          <UnstyledButton
            w="100%" px={4} py={2}
            style={{ paddingLeft: depth * 12 + 4 }}
            onClick={() => {
              if (node.hasChildren) setOpen(o => ({ ...o, [node.ref]: !o[node.ref] }));
              onSelect({ connectionId: conn, node });
            }}>
            <Group gap={4} wrap="nowrap">
              {node.hasChildren
                ? (open[node.ref] ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />)
                : <span style={{ width: 12 }} />}
              {nodeIcon(node.kind)}
              <Text size="xs" truncate>{node.label}</Text>
            </Group>
          </UnstyledButton>
          {node.hasChildren && open[node.ref] && (
            <TreeLevel conn={conn} parent={node.ref} depth={depth + 1} filter="" onSelect={onSelect} />
          )}
        </div>
      ))}
    </>
  );
}

export function ExplorerTree({ onSelect }: { onSelect: (s: ExplorerSelection) => void }) {
  const [connections, setConnections] = useState<Connection[]>([]);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [filter, setFilter] = useState("");
  const [nonce, setNonce] = useState(0);

  useEffect(() => { listConnections().then(setConnections).catch(() => setConnections([])); }, [nonce]);

  return (
    <div style={{ height: "100%", overflow: "auto" }}>
      <Group gap={4} p={4} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="Filter" leftSection={<IconSearch size={13} />}
          value={filter} onChange={e => setFilter(e.currentTarget.value)} />
        <ActionIcon size="sm" variant="subtle" onClick={() => setNonce(n => n + 1)} title="Refresh">
          <IconRefresh size={14} />
        </ActionIcon>
      </Group>

      {connections.map(c => (
        <div key={`${c.id}-${nonce}`}>
          <UnstyledButton w="100%" px={4} py={3} onClick={() => setOpen(o => ({ ...o, [c.id]: !o[c.id] }))}>
            <Group gap={4} wrap="nowrap">
              {open[c.id] ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
              <Text size="xs" fw={600} c={c.color ?? undefined} truncate>{c.name}</Text>
              {c.readOnly && <Badge size="xs" variant="light" color="orange">RO</Badge>}
            </Group>
          </UnstyledButton>
          {open[c.id] && <TreeLevel conn={c.id} depth={1} filter={filter} onSelect={onSelect} />}
        </div>
      ))}
    </div>
  );
}
```

- [ ] **Step 7: Write the detail panel**

`web/src/explorer/ObjectDetailPanel.tsx`:

```tsx
import { useEffect, useState } from "react";
import { Badge, Group, Loader, ScrollArea, Table, Tabs, Text } from "@mantine/core";
import { describeObject, type ObjectDetailDto } from "../api";
import type { ExplorerSelection } from "./ExplorerTree";

export function ObjectDetailPanel({ selection }: { selection: ExplorerSelection | null }) {
  const [detail, setDetail] = useState<ObjectDetailDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setDetail(null);
    setError(null);
    if (!selection || !["Table", "View", "MaterializedView"].includes(selection.node.kind)) return;
    describeObject(selection.connectionId, selection.node.ref).then(setDetail).catch(e => setError(e.message));
  }, [selection]);

  if (!selection) return <Text size="xs" c="dimmed" p="xs">Select an object.</Text>;
  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!detail) return <Loader size="xs" m="xs" />;

  return (
    <Tabs defaultValue="columns" h="100%">
      <Tabs.List>
        <Tabs.Tab value="columns">Columns</Tabs.Tab>
        <Tabs.Tab value="indexes">Indexes</Tabs.Tab>
        <Tabs.Tab value="keys">Foreign keys</Tabs.Tab>
        <Tabs.Tab value="info">Info</Tabs.Tab>
      </Tabs.List>

      <Tabs.Panel value="columns">
        <ScrollArea h="calc(100% - 36px)">
          <Table striped stickyHeader fz="xs">
            <Table.Thead><Table.Tr>
              <Table.Th>Name</Table.Th><Table.Th>Type</Table.Th><Table.Th>Null</Table.Th><Table.Th>Default</Table.Th>
            </Table.Tr></Table.Thead>
            <Table.Tbody>
              {detail.columns.map(c => (
                <Table.Tr key={c.name}>
                  <Table.Td>
                    <Group gap={4}>{c.name}{c.isPrimaryKey && <Badge size="xs" variant="light">PK</Badge>}</Group>
                  </Table.Td>
                  <Table.Td>{c.dataType}</Table.Td>
                  <Table.Td>{c.nullable ? "yes" : "no"}</Table.Td>
                  <Table.Td>{c.default ?? ""}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </ScrollArea>
      </Tabs.Panel>

      <Tabs.Panel value="indexes">
        <Table fz="xs">
          <Table.Tbody>
            {detail.indexes.map(i => (
              <Table.Tr key={i.name}>
                <Table.Td>{i.name}</Table.Td>
                <Table.Td>{i.columns.join(", ")}</Table.Td>
                <Table.Td>{i.unique ? "unique" : ""}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Tabs.Panel>

      <Tabs.Panel value="keys">
        <Table fz="xs">
          <Table.Tbody>
            {detail.foreignKeys.map(f => (
              <Table.Tr key={f.name}>
                <Table.Td>{f.columns.join(", ")}</Table.Td>
                <Table.Td>→ {f.referencedTable}({f.referencedColumns.join(", ")})</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Tabs.Panel>

      <Tabs.Panel value="info">
        <Text size="xs" p="xs">
          Rows: {detail.rowCount ?? "unknown"}<br />
          Size: {detail.sizeBytes ? `${Math.round(detail.sizeBytes / 1024)} KiB` : "unknown"}<br />
          {detail.comment}
        </Text>
      </Tabs.Panel>
    </Tabs>
  );
}
```

- [ ] **Step 8: Write the dock shell**

`web/src/dock/DockShell.tsx`:

```tsx
import { useState } from "react";
import { DockviewReact } from "dockview-react";
import type { DockviewReadyEvent, IDockviewPanelProps } from "dockview-react";
import "dockview-react/dist/styles/dockview.css";
import "../editor/dockview-mantine.css";
import { useAppTheme } from "../ThemeProvider";
import { ExplorerTree, type ExplorerSelection } from "../explorer/ExplorerTree";
import { ObjectDetailPanel } from "../explorer/ObjectDetailPanel";

const components = {
  structure: (props: IDockviewPanelProps<{ selection: ExplorerSelection | null }>) =>
    <ObjectDetailPanel selection={props.params.selection} />,
};

export function DockShell() {
  const { current } = useAppTheme();
  const [selection, setSelection] = useState<ExplorerSelection | null>(null);

  const onReady = (event: DockviewReadyEvent) => {
    event.api.addPanel({ id: "structure", component: "structure", title: "Structure", params: { selection: null } });
  };

  // Pushing the selection through panel params keeps dockview as the single owner of panel state.
  const select = (s: ExplorerSelection) => {
    setSelection(s);
    document.dispatchEvent(new CustomEvent("wds:selection", { detail: s }));
  };

  return (
    <div style={{ display: "flex", height: "100%" }}>
      <div style={{ width: 280, borderRight: "1px solid var(--mantine-color-default-border)" }}>
        <ExplorerTree onSelect={select} />
      </div>
      <div style={{ flex: 1 }}>
        <DockviewReact className={current.dockview} components={components} onReady={onReady} />
      </div>
      <div style={{ width: 300, borderLeft: "1px solid var(--mantine-color-default-border)" }}>
        <ObjectDetailPanel selection={selection} />
      </div>
    </div>
  );
}
```

- [ ] **Step 9: Route between connections and the studio**

Change `App.tsx` so `/` renders `<DockShell />` and `/connections` renders `<ConnectionsPage />`,
with a link between them in `AppShellFrame`.

- [ ] **Step 10: Verify by hand**

Start the server with the SQLite demo connection from P0 Task 7 Step 6 and open the dev server.
Expected: the connection expands, Tables lists `people` and `orders`, clicking `people` fills the
Structure panel with columns, the primary key badge and the foreign key of `orders`.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: object explorer with lazy tree and structure panel"
```

---

## Phase exit criteria

- The contract suite passes for all four tier-1 engines (`dotnet test` with Docker running).
- The capability honesty test passes.
- The explorer shows schemas, tables, views, procedures, functions and sequences for every tier-1
  engine, with columns, indexes and foreign keys in the structure panel.
- `POST /api/query/execute` streams NDJSON, a syntax error arrives as an error line rather than a
  500, and `POST /api/query/{runId}/cancel` stops a running query.
- A read-only connection refuses `DELETE` with a clear message.
- Feature IDs F2.1–F2.6, F4.1–F4.4 and F4.6 are demonstrably working.
