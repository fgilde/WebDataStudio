# WebDataStudio — Design

Status: approved 2026-08-18
Image: `ghcr.io/fgilde/webdatastudio`

## 1. Purpose

A web-based database management studio in the spirit of DbGate, DataGrip, SQL Server
Management Studio and phpMyAdmin. It runs as a single Docker container, supports as many
database engines as possible, and looks and behaves like AspireUI so that anyone who knows
that tool is immediately at home.

Non-goals for this design: AI / text-to-SQL, cloud sync of connections, multi-user role
management. Each would be a separate design if wanted later.

## 2. Constraints

- One Docker image, one process, published to GHCR from GitHub Actions.
- Connections must be injectable through environment variables and attached at startup.
- Connections must also be manageable comfortably in the UI.
- Authentication: a single account from environment variables, or anonymous access with no
  login screen when those variables are absent.
- Monaco is the query editor. Selected text is executable on its own.
- Result tables must be exportable to CSV, Excel and every other format that makes sense.
- Themes, dockview layouts and general UI feel are taken from AspireUI.

## 3. Reused from AspireUI

`C:\dev\privat\github\AspireUI` is the reference implementation for the shell. These pieces
are copied rather than reinvented:

| Piece | Source | Use |
|---|---|---|
| Theme system | `web/src/themes.ts`, `ThemeProvider.tsx`, `ThemeDrawer.tsx`, `ThemeMenu.tsx` | 21 themes, each binding a Mantine theme to a dockview theme class and a Monaco theme |
| Dock layout | `web/src/editor/DockLayout.tsx`, `dockview-mantine.css` | dockview wiring and Mantine-matched panel chrome |
| Command palette | `web/src/CommandPalette.tsx` (Mantine Spotlight) | Ctrl/Cmd+K |
| Shortcut help | `web/src/ShortcutsHelp.tsx` | keyboard reference overlay |
| Page shell | `web/src/components/PageShell.tsx` | header, nav, content frame |
| Auth flow | `src/AspireUI.Server/Endpoints/AuthEndpoints.cs`, `Services/ApiKeyAuthenticationHandler.cs` | cookie auth returning 401 instead of redirecting |
| Docker + CI | `Dockerfile`, `.github/workflows/docker-publish.yml` | multi-stage SDK build with SPA compile, multi-arch push to GHCR |
| Diagram stack | `@xyflow/react` + `dagre` (already dependencies) | ER diagrams |

## 4. Repository layout

```
WebDataStudio/
  src/WebDataStudio.Server/          net10.0, Microsoft.NET.Sdk.Web
    Drivers/
      Abstractions/                  IDbDriver, capabilities, dialect, result model
      PostgreSql/  MySql/  SqlServer/  Sqlite/
      Oracle/  DuckDb/  ClickHouse/
      MongoDb/  Redis/
      DriverRegistry.cs
    Endpoints/                       minimal API endpoint groups
    Models/
    Services/                        ConnectionStore, SecretProtector, QueryRunner,
                                     ExportService, ImportService, AnalyzeService
    wwwroot/                         SPA build output (Release only)
  web/                               React 19 + Mantine 9 + dockview + Monaco + Vite
  tests/
    WebDataStudio.Server.Tests/      driver contract tests, endpoint tests
  docs/superpowers/specs/
  Dockerfile
  docker-compose.yml
  Directory.Packages.props
  .github/workflows/docker-publish.yml
```

The server project mirrors `AspireUI.Server`: a `BuildSpa` MSBuild target runs
`npm install && npm run build` for Release and copies `web/dist` into `wwwroot`. Debug builds
stay node-free.

## 5. Architecture

```
Browser (React SPA)
  │  REST + NDJSON streams
  ▼
WebDataStudio.Server (net10.0 minimal API)
  ├── AuthEndpoints        cookie auth, env-configured single account or anonymous
  ├── ConnectionEndpoints  CRUD + test, merges env-defined and stored connections
  ├── SchemaEndpoints      lazy introspection per tree level
  ├── QueryEndpoints       execute / cancel / plan / analyze
  ├── DataEndpoints        paged table browse, edit with change-script preview
  ├── ExportEndpoints      streaming export, all formats
  ├── AdminEndpoints       system commands, backup/restore, users, sessions
  └── DriverRegistry ──► IDbDriver per engine ──► ADO.NET / native client
        │
        └── ConnectionStore (SQLite at /data/webdatastudio.db, AES-GCM secrets)
```

### 5.1 Driver abstraction

Every engine implements one interface. Behaviour differences are expressed as capability
flags, never as engine-specific branches in endpoints or UI.

```csharp
public interface IDbDriver
{
    DriverInfo Info { get; }                 // id, label, icon, default port, conn-string template
    DriverCapabilities Caps { get; }
    SqlDialect Dialect { get; }
    IDdlWriter Ddl { get; }

    Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct);
    Task<SchemaNode[]> IntrospectAsync(IDbSession s, SchemaNodeRef parent, CancellationToken ct);
    IAsyncEnumerable<ResultChunk> ExecuteAsync(IDbSession s, ScriptRequest req, CancellationToken ct);
    Task<PlanNode> ExplainAsync(IDbSession s, string sql, PlanMode mode, CancellationToken ct);
    Task<AnalyzeReport> AnalyzeAsync(IDbSession s, AnalyzeScope scope, CancellationToken ct);
}
```

`Ddl` arrives later than the rest: nothing writes DDL before the SQL schema exporter (P3), and the
full writer interface only becomes necessary with the designer (P6).

`DriverCapabilities` is a flags record covering at minimum: `Sql`, `MultiSchema`,
`MultiDatabase`, `EstimatedPlan`, `ActualPlan`, `Transactions`, `Ddl`, `StoredProcedures`,
`Triggers`, `Views`, `MaterializedViews`, `Sequences`, `ForeignKeys`, `PartialIndexes`,
`IncludeColumns`, `Backup`, `Restore`, `UserManagement`, `SessionList`, `KillSession`,
`ServerStats`, `SlowQueryLog`, `SystemCommands`.

The UI reads the capability set of the active connection and hides what is unsupported.
No disabled buttons that never become enabled.

`SqlDialect` carries identifier quoting, string escaping, paging syntax, boolean literals,
parameter prefix, and the reserved-word set used by the formatter and the DDL writer.

### 5.2 Capability tiers

| Tier | Engines | Notes |
|---|---|---|
| 1 | PostgreSQL, MySQL/MariaDB, SQL Server, SQLite | full surface: introspection, plans, DDL, transactions |
| 2 | Oracle, DuckDB, ClickHouse | same interface, reduced capability sets |
| 3 | MongoDB, Redis | `Caps.Sql = false`; own editor mode and result renderer |

Tier 3 does not fake SQL. `Caps.Sql = false` switches the Monaco language, the execution
request shape and the result renderer (JSON tree instead of grid), while connection handling,
history, export and the shell stay identical.

Provider packages: Npgsql, MySqlConnector, Microsoft.Data.SqlClient,
Microsoft.Data.Sqlite, Oracle.ManagedDataAccess.Core, DuckDB.NET.Data.Full,
ClickHouse.Client, MongoDB.Driver, StackExchange.Redis.

### 5.3 Result streaming

Query results are streamed, never fully materialised on the server. `ExecuteAsync` yields
`ResultChunk` values that the endpoint writes as NDJSON:

```
{"type":"columns","statement":0,"columns":[{"name":"id","type":"int4","nullable":false}]}
{"type":"rows","statement":0,"rows":[[1,"a@b.de",true],[2,"c@d.de",true]]}
{"type":"progress","statement":0,"rowsRead":2000,"elapsedMs":412}
{"type":"message","statement":0,"severity":"notice","text":"..."}
{"type":"end","statement":0,"rowsAffected":0,"elapsedMs":34,"truncated":true}
{"type":"error","statement":1,"text":"...","line":3,"column":12}
```

The client renders chunks as they arrive. Cancellation is a `POST /api/query/{runId}/cancel`
that trips the `CancellationTokenSource` held for that run.

## 6. Connections

### 6.1 Environment variables

| Variable | Meaning |
|---|---|
| `WDS_CONNECTIONS` | JSON array of connection objects, applied at startup |
| `WDS_CONN_<NAME>` | single connection as a URL, e.g. `postgres://user:pw@host:5432/db` |
| `WDS_USER`, `WDS_PASSWORD` | when both are set, a login screen guards the app; otherwise anonymous |
| `WDS_SECRET_KEY` | AES key for stored connection secrets; generated into `/data/.key` if absent |
| `DB_PATH` | path of the application SQLite database, default `/data/webdatastudio.db` |
| `WDS_QUERY_TIMEOUT_SECONDS` | default statement timeout, default 300 |
| `WDS_MAX_ROWS` | default fetch cap per result, default 1000 |
| `WDS_READONLY` | when true, every connection is read-only regardless of its own flag |
| `ASPNETCORE_URLS` | listen address, default `http://0.0.0.0:8080` |

`WDS_CONNECTIONS` element shape:

```json
{
  "name": "prod-pg",
  "engine": "postgresql",
  "connectionString": "Host=db;Port=5432;Database=shop;Username=app;Password=secret",
  "readOnly": true,
  "color": "red",
  "group": "Production"
}
```

The URL form of `WDS_CONN_<NAME>` derives the engine from the scheme (`postgres`,
`postgresql`, `mysql`, `mariadb`, `sqlserver`, `mssql`, `sqlite`, `oracle`, `duckdb`,
`clickhouse`, `mongodb`, `redis`).

Environment connections are merged on every start, are read-only in the UI and carry a badge.
They cannot be edited or deleted from the UI.

### 6.2 Secret handling

Stored connection passwords are encrypted with AES-GCM. The key comes from
`WDS_SECRET_KEY`; if unset, a random key is generated once and written to `/data/.key` with
owner-only permissions. Secrets are never returned to the client, never written to logs, and
never included in connection exports.

## 7. HTTP API

| Method + path | Purpose |
|---|---|
| `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me` | session; `me` reports `anonymous: true` when no credentials are configured |
| `GET /api/connections` | merged list, secrets stripped, capability set per entry |
| `POST /api/connections`, `PUT/DELETE /api/connections/{id}` | UI-managed connections only |
| `POST /api/connections/test` | probe without saving |
| `GET /api/drivers` | engine catalogue with form metadata and capabilities |
| `GET /api/schema/{conn}?parent=` | one tree level, lazy |
| `GET /api/schema/{conn}/object/{ref}` | columns, indexes, foreign keys, triggers, size, DDL |
| `GET /api/schema/{conn}/object/{ref}/dependencies` | both directions |
| `POST /api/query/execute` | NDJSON stream; body carries sql, connection, schema, params, maxRows |
| `POST /api/query/{runId}/cancel` | cancel a running statement |
| `POST /api/query/plan` | estimated or actual plan as a node tree |
| `POST /api/query/analyze` | index advisor and deep-analyze report |
| `GET /api/data/{conn}/{ref}` | paged rows with filter and sort |
| `POST /api/data/{conn}/{ref}/preview-changes` | change script for a pending edit set |
| `POST /api/data/{conn}/{ref}/apply-changes` | execute a previously previewed change set |
| `POST /api/export/{format}` | streaming export of a query or an object |
| `POST /api/import` | CSV/Excel/JSON/SQL import with column mapping |
| `GET/POST /api/history` | query history |
| `GET/POST/DELETE /api/saved-queries` | bookmarks with folders |
| `GET /api/admin/sessions`, `POST /api/admin/sessions/{id}/kill` | session control |
| `POST /api/admin/system-command` | capability-gated maintenance commands |
| `POST /api/admin/backup`, `POST /api/admin/restore` | capability-gated |
| `GET /api/admin/stats` | server metrics |
| `POST /api/compare/schema`, `POST /api/compare/data` | diff and sync script |

## 8. Frontend

React 19, Mantine 9, dockview, Monaco, Vite — the same versions AspireUI pins.

Shell shape (DataGrip-like, approved):

- Left: object explorer, fixed panel, all connections in one tree.
- Centre: query tabs, each split into a Monaco editor above and a result area below.
- Right: dockable context panels — Structure, Plan, Advisor, History.
- Everything except the explorer is dockview-movable. Layouts are saved per connection and a
  "Reset layout" action is always reachable.

Theme handling is the AspireUI mechanism unchanged: the active `AppTheme` supplies the
Mantine theme, the dockview theme class and the Monaco theme together, so switching a theme
restyles the editor, the panels and the grid in one step.

## 9. Feature inventory

The implementation checklist. Every phase plan references these IDs; an ID is done only when
it works for every engine whose capability set claims support for it.

### F1 Connections
| ID | Feature |
|---|---|
| F1.1 | Environment connections (`WDS_CONNECTIONS` array and `WDS_CONN_<NAME>` URLs), read-only badge |
| F1.2 | UI CRUD with a per-engine form; pasting a connection string detects the engine and fills the form |
| F1.3 | Test connection before saving |
| F1.4 | SSH tunnel, SSL/TLS options, client certificates |
| F1.5 | Connection folders/groups and colour marking (production = red) |
| F1.6 | Per-connection read-only flag, enforced in the driver |
| F1.7 | Connection pooling, auto-reconnect, idle timeout |
| F1.8 | Import/export of connection definitions as JSON, without secrets |

### F2 Object explorer
| ID | Feature |
|---|---|
| F2.1 | Lazy tree: server, database, schema, tables, views, materialised views, procedures, functions, triggers, sequences, indexes, constraints, types, users |
| F2.2 | Tree filter and fuzzy search, "go to object" (Ctrl+Shift+O) |
| F2.3 | Context menu: open data, show DDL, rename, drop, truncate, generate SELECT/INSERT/UPDATE/DELETE/CREATE script |
| F2.4 | Object detail panel: columns, indexes, foreign keys, triggers, size, row count, comments |
| F2.5 | Dependencies in both directions |
| F2.6 | Several connections open at once |

### F3 Query editor
| ID | Feature |
|---|---|
| F3.1 | Dialect-aware SQL highlighting |
| F3.2 | Schema-aware completion: tables, columns, aliases, functions, keywords |
| F3.3 | Execute selection (F5 / Ctrl+Enter); with no selection, execute the statement under the cursor |
| F3.4 | Statement boundary detection with the active statement visibly highlighted |
| F3.5 | Dialect-aware formatter |
| F3.6 | Inline error highlighting, server errors mapped to line and column |
| F3.7 | Go-to-definition on table names, hover shows columns |
| F3.8 | Query parameters (`:name`, `@name`) with an input dialog |
| F3.9 | Snippets and user-defined live templates |
| F3.10 | Persisted, searchable, restorable query history |
| F3.11 | Saved queries and bookmarks with folders |
| F3.12 | Multi-cursor, find and replace, regex |
| F3.13 | Tabs survive reload and container restart |
| F3.14 | Visual query designer / query-by-example |

### F4 Execution
| ID | Feature |
|---|---|
| F4.1 | Streaming results rendered while the query is still running |
| F4.2 | Cancel a running query |
| F4.3 | Multiple statements in sequence, one result tab per statement |
| F4.4 | Messages tab: notices, warnings, `PRINT`, `RAISE NOTICE`, affected rows |
| F4.5 | Explicit transaction control: auto-commit toggle, commit and rollback actions |
| F4.6 | Timing per statement and total |
| F4.7 | Session/process list, kill foreign sessions |
| F4.8 | Configurable query timeout and row cap, overridable per run |

### F5 Result grid
| ID | Feature |
|---|---|
| F5.1 | Virtualised grid handling hundreds of thousands of rows |
| F5.2 | Sort, per-column filter, full-text search within the result |
| F5.3 | Hide, reorder, pin and resize columns with persisted widths |
| F5.4 | Cell value viewer: text, JSON, XML, hex, image, BLOB download |
| F5.5 | NULL visually distinct from an empty string |
| F5.6 | Selection aggregate (sum, average, count) in the status bar |
| F5.7 | Form view for rows with many columns |
| F5.8 | Master-detail over foreign keys; clicking a foreign key jumps to the target row |
| F5.9 | Grid grouping |
| F5.10 | Compare two result sets |
| F5.11 | Charts from a result (bar, line, pie) |
| F5.12 | Transposed view |

### F6 Data editing
| ID | Feature |
|---|---|
| F6.1 | Inline editing in the grid, spreadsheet-like, batched |
| F6.2 | Insert, delete and duplicate rows |
| F6.3 | Change-script preview before applying — always, not optional |
| F6.4 | Tables without a primary key: editing blocked with a clear reason |
| F6.5 | Foreign-key lookup dropdown while editing |
| F6.6 | Bulk update over a selection via macro or expression |

### F7 Export and import
| ID | Feature |
|---|---|
| F7.1 | Export to CSV, TSV, Excel (xlsx), JSON, NDJSON, XML, YAML, Markdown, HTML, SQL INSERTs, SQL CREATE+INSERT, Parquet |
| F7.2 | Export scope: selection, current page, whole result, whole table, whole schema |
| F7.3 | Streaming export with no server-side materialisation |
| F7.4 | Export options: delimiter, encoding, quoting, header row, NULL representation, date format |
| F7.5 | Copy as CSV / JSON / SQL IN-list / Markdown table to the clipboard |
| F7.6 | Import from CSV, Excel, JSON and SQL with column mapping and preview |
| F7.7 | Table-to-table copy, including across engines |

### F8 Schema editing
| ID | Feature |
|---|---|
| F8.1 | Show and edit the DDL of any object |
| F8.2 | Table designer: columns, types, nullability, defaults, identity, comments |
| F8.3 | Index management: create, alter, drop; unique, partial and include columns where supported |
| F8.4 | Constraints: primary key, foreign key, unique, check |
| F8.5 | Create and alter views, procedures, functions and triggers |
| F8.6 | Migration script preview for every change; execution only after confirmation |
| F8.7 | Rename an object with a dependency warning |

### F9 Performance and analysis
| ID | Feature |
|---|---|
| F9.1 | Estimated plan and actual plan |
| F9.2 | Plan as a tree plus a graphical view with a cost heat map |
| F9.3 | Expensive nodes highlighted: sequential scan on a large table, missing index, spill to disk |
| F9.4 | Index advisor producing concrete `CREATE INDEX` statements with a rationale |
| F9.5 | Deep analyze: missing, unused and duplicate indexes, table bloat, stale statistics, unindexed foreign keys |
| F9.6 | Table statistics: size, row count, index size, last vacuum/analyze |
| F9.7 | Slow query view where the engine provides one (pg_stat_statements, Query Store, performance_schema) |
| F9.8 | Server metrics: connections, cache hit ratio, locks, blocking chains |

### F10 Compare and deploy
| ID | Feature |
|---|---|
| F10.1 | Schema diff between two connections with a generated sync script |
| F10.2 | Data diff between two tables with a generated sync script |
| F10.3 | Diff rendered in the Monaco diff editor |

### F11 Administration
| ID | Feature |
|---|---|
| F11.1 | Backup and restore where the engine supports it (pg_dump, mysqldump, `BACKUP DATABASE`, SQLite `.backup`) |
| F11.2 | User and privilege management |
| F11.3 | Capability-gated system commands: VACUUM, ANALYZE, REINDEX, OPTIMIZE, CHECKDB, FLUSH, configuration variables |
| F11.4 | Server log viewer where reachable |
| F11.5 | Create and drop databases |

### F12 Diagrams
| ID | Feature |
|---|---|
| F12.1 | ER diagram per schema using `@xyflow/react` and `dagre` |
| F12.2 | Table selection, auto layout, export as PNG and SVG |
| F12.3 | Clicking a table in the diagram opens its data or structure |

### F13 Usability
| ID | Feature |
|---|---|
| F13.1 | Command palette (Ctrl+K) covering every action |
| F13.2 | Keyboard shortcuts throughout with a shortcut help overlay |
| F13.3 | The 21 AspireUI themes and the theme drawer |
| F13.4 | Save and reset layout presets |
| F13.5 | Deep links to an object or a query |
| F13.6 | Toasts for long-running jobs |

### F14 NoSQL (tier 3)
| ID | Feature |
|---|---|
| F14.1 | MongoDB: collection browser, JSON editor, aggregation pipeline, index management |
| F14.2 | Redis: key tree, type-specific editors, TTL, command console |
| F14.3 | Result renderer as a JSON tree, switchable to a table for flat documents |

## 10. Safety behaviour

- Statement timeout and row cap apply to every run; the cap is visible in the result footer
  ("1000 of ~1.2M rows, load more").
- The per-connection read-only flag is enforced in the driver, not in the client. A read-only
  connection rejects anything the dialect classifies as DML or DDL.
- `WDS_READONLY=true` forces read-only on every connection, including environment ones.
- UPDATE and DELETE without a WHERE clause require an explicit confirmation showing the
  affected row count, obtained inside a transaction that is rolled back, where the engine
  supports it.
- Every schema change and every grid edit goes through a change-script preview before it
  executes.
- Secrets never reach logs, never reach the client, never enter exports.

## 11. Error handling

- Driver errors map to a common `QueryError` carrying message, engine error code, and
  line/column when the engine reports a position, so Monaco can mark the exact spot.
- Connection failures surface as a connection-level state in the explorer (offline badge,
  retry action) rather than as a modal per request.
- A cancelled query is a normal outcome, not an error: the result tab shows the partial rows
  and a "cancelled" marker.
- Endpoint failures return RFC 7807 problem details.

## 12. Testing

- **Driver contract tests** — one shared xUnit suite executed against every tier-1 and
  tier-2 driver via Testcontainers, asserting identical behaviour for introspection,
  execution, streaming, cancellation, paging, plans and DDL generation. A new engine is done
  when it passes the contract suite for the capabilities it claims.
- **Capability honesty test** — for each driver, assert that every capability set to true is
  actually implemented and that every capability set to false throws `NotSupportedException`
  rather than failing obscurely.
- **Endpoint tests** — `WebApplicationFactory` against SQLite for the auth, connection,
  schema, query and export endpoints.
- **Frontend** — Vitest for the statement splitter, the dialect completion source, the export
  formatters and the change-script builder.
- Every phase ends with the checklist IDs it claims verified by a runnable test or by a manual
  check recorded in the phase plan.

## 13. Delivery

- `Dockerfile`: multi-stage, SDK image with Node for the SPA build, runtime image with the
  ASP.NET runtime plus the CLI tools required by F11.1 (`pg_dump`/`pg_restore`, `mysqldump`,
  `mongodump`, `redis-cli`), exposing 8080 with `VOLUME ["/data"]`.
- `.github/workflows/docker-publish.yml`: multi-arch build (amd64 + arm64), pushing `latest`
  for the default branch, a version tag for git tags and a short-sha tag for every build, to
  `ghcr.io/fgilde/webdatastudio`.
- `docker-compose.yml`: the image plus example environment connections, for local trial.

## 14. Phases

Each phase is planned and implemented separately and ends in a working, shippable image.

| Phase | Content | Feature IDs |
|---|---|---|
| P0 | Skeleton: repository, .NET 10 server, SPA shell with AspireUI themes, Dockerfile, GHCR workflow, auth, connection store | F1.1–F1.3, F1.6, F13.3 |
| P1 | Driver abstraction and tier 1 (PostgreSQL, MySQL, SQL Server, SQLite): introspection, execution, streaming | F2.1–F2.6, F4.1–F4.4, F4.6 |
| P2 | Query editor and result grid: Monaco, selection execution, completion, virtualised grid | F3.1–F3.7, F3.10, F3.12, F3.13, F4.8, F5.1–F5.7 |
| P3 | Export and import | F7.1–F7.7 |
| P4 | Data editing with change-script preview and transaction control | F4.5, F5.8, F6.1–F6.6 |
| P5 | Execution plans, index advisor, deep analyze | F9.1–F9.8 |
| P6 | Schema editing and DDL designer | F8.1–F8.7 |
| P7 | Tier 2 (Oracle, DuckDB, ClickHouse) and tier 3 (MongoDB, Redis) | F14.1–F14.3, capability extensions |
| P8 | ER diagrams, compare and deploy, backup and restore, administration, session control | F4.7, F10.1–F10.3, F11.1–F11.5, F12.1–F12.3 |
| P9 | Remaining usability: query designer, charts, macros, layout presets, SSH tunnel, parameters, snippets | F1.4, F1.5, F1.7, F1.8, F3.8, F3.9, F3.11, F3.14, F5.9–F5.12, F13.1, F13.2, F13.4–F13.6 |

The tool is usable after P2, replaces phpMyAdmin after P4, and covers day-to-day DataGrip
work after P6.
