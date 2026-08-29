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
| F2.7 | A connection's health in the tree: reachable, how long it took, why not |
| F2.8 | PostgreSQL LISTEN/NOTIFY: watch the channels, and send one |
| F2.9 | A data dictionary: tables, columns, keys and notes as one document |

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
| F6.4 | Tables without a primary key: a unique index, then the engine's own row address; blocked with a clear reason where there is neither |
| F6.5 | Foreign-key lookup dropdown while editing |
| F6.6 | Bulk update over a selection via macro or expression |
| F6.7 | Paste rows from the clipboard as inserts |
| F6.8 | Keep a result as a new table, here or in another connection |

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
| F12.4 | Backups on a schedule, kept and pruned per job |
| F12.5 | Seed a connection from another one at start |

### F13 Usability
| ID | Feature |
|---|---|
| F13.1 | Command palette (Ctrl+K) covering every action |
| F13.2 | Keyboard shortcuts throughout with a shortcut help overlay |
| F13.3 | The 21 AspireUI themes and the theme drawer |
| F13.4 | Save and reset layout presets |
| F13.5 | Deep links to an object or a query |
| F13.6 | Toasts for long-running jobs |
| F13.7 | Every panel dockable, the explorer included, with a way back to it |
| F13.8 | The running build visible in the studio and over the API |
| F13.9 | A draggable split between statement and result, and editor text zoom |
| F13.10 | Closing many tabs at once leaves the studio's own panels standing |

### F14 NoSQL (tier 3)
| ID | Feature |
|---|---|
| F14.1 | MongoDB: collection browser, JSON editor, aggregation pipeline, index management |
| F14.2 | Redis: key tree, type-specific editors, TTL, command console |
| F14.3 | Result renderer as a JSON tree, switchable to a table for flat documents |
| F14.7 | Look at a file rather than download it, through an on-demand viewer |

### F15 Documentation site
| ID | Feature |
|---|---|
| F15.1 | GitHub Pages site served from `/docs`, docsify like AspireUI, same visual language |
| F15.2 | Every page in English and German, switchable in the sidebar; docsify i18n through a `/docs/de/` tree |
| F15.3 | Content: getting started, environment variables, connections, query editor, results and export, schema editing, analysis, administration, engine capability matrix, shortcuts |
| F15.4 | Screenshots per major feature, captured in a dark and a light theme |
| F15.5 | README links to the site in both languages; the site links back to the repository and the GHCR image |
| F15.6 | A link check in CI so a renamed page cannot silently break the sidebar |
| F16.1 | Entra authentication for a managed database, resolved from the container's identity |
| F16.2 | A studio that keeps working when its own storage does not, and says so |
| F17.1 | A visual query builder that works like a canvas rather than a form |
| F17.2 | Joins the schema already knows are proposed, not typed |
| F17.3 | The query being built is visible as SQL and as rows |
| F17.4 | Aggregates, grouping, HAVING and DISTINCT from the builder |
| F17.5 | A generated query can be reopened in the builder that made it |
| F23.1 | Browsing a table's data has the same column controls as a query result |
| F23.2 | The explorer searches for objects, not for the level it happens to show |
| F23.3 | Panels can be closed in groups, pinned, and moved into their own window |
| F19.1 | A Redis keyspace is browsed by scanning, never by listing it |
| F19.2 | Every Redis type is edited in the shape it has |
| F19.3 | A Redis write is shown as the commands it will run before it runs |
| F19.4 | Deleting or expiring by pattern acts on the set that was approved |
| F19.5 | Where the memory in a keyspace went, sampled rather than exhaustive |
| F19.6 | Pub/sub and stream consumer groups are visible while they happen |
| F19.7 | The slow log and the client list of a Redis server |
| F19.8 | The console completes from the server's own command help, and the cluster is visible |
| F18.1 | One view that answers what the server is doing right now |
| F18.2 | Long-running work is visible, with progress where the engine knows it |
| F18.3 | A lock is shown as the chain it is, so the right session gets killed |
| F18.4 | A recommendation can be applied, not only read |
| F18.5 | Replicas and their lag |
| F18.6 | Where the disk went, as areas rather than a list |
| F20.1 | A column that holds a secret is masked before it leaves the server |
| F20.2 | An applied data change can be taken back, script first |
| F20.3 | Several accounts, each with a role and the connections it may see |
| F20.4 | The deployment can say which columns are secret, and which are not |
| F21.1 | A query can join two connections, by staging their rows |
| F21.2 | A query can be watched, and what changed is visible |
| F21.3 | An empty table can be filled with plausible rows |
| F21.4 | SQL, prose and results in one document that can be saved and pasted |
| F22.1 | Optional assistance that explains a statement or drafts one, off unless configured |
| F22.2 | The studio answers as an MCP server, with the same rules a person gets |
| F22.3 | The assistant can answer from the database through those same tools |
| F22.4 | A chat with sessions, in the corner, using those tools where they exist |
| F22.5 | An agent can ask why something is slow, and what the studio thinks is wrong |
| F22.6 | A deployment decides which tools its agents get |
| F24.1 | Somebody hears about a health finding without opening the studio |
| F24.2 | A schema that moved without a migration is noticed |
| F24.3 | The queries a team shares live in its repository, not in a chat |
| F24.4 | A fresh stack comes up with data in it |
| F24.5 | A report runs itself, and reads only |
| F24.6 | "Here is what I am seeing" is a link, not a screenshot |
| F24.7 | The studio is visible in the same collector as everything else |
| F25.1 | An object says what it costs and which of its indexes anybody reads |
| F25.2 | Who may do what to an object, and the statement that changes it |
| F25.3 | What breaks if this object changes |
| F25.4 | Any object can be read as the statement that creates it |
| F25.5 | A plan that spilled to disk says so |
| F26.1 | The tree shows the server's own objects: extensions, roles, tablespaces, publications, subscriptions, types |
| F26.2 | Privileges for everything in a schema at once, including what is created later |
| F26.3 | A materialised view can be refreshed, with or without blocking readers |
| F26.4 | The dashboard draws its numbers over time, not only the last reading |
| F26.5 | A backup says which format, which flags, and how many bytes have arrived |
| F26.6 | A function can be read, run against a rolled-back transaction, and its notices seen |
| F26.7 | Row-level security: whether it is on, what the policies say, and how to change them |
| F26.8 | A partitioned table shows its pieces and can hand one over or take one back |
| F26.9 | Preferences that survive a restart, including rebinding any command's shortcut |
| F26.10 | A history entry can keep the result it returned, and show it again |
| F27.1 | A column filter is a small language, not a substring: operators, dates, NULL, AND and OR |
| F27.2 | The values a column actually holds, as checkboxes |
| F27.3 | A column from the table a foreign key points at, shown next to the id |
| F27.4 | A row and everything related to it, nested as deep as you open it |
| F27.5 | A result kept as a file, listed, reopened, and scripted back as INSERTs |
| F27.6 | Geography in a result drawn as a shape rather than read as coordinates |
| F27.7 | "there is no row over there" as a condition in the query builder |
| F28.1 | Object storage as a connection: S3-compatible, Azure Blob, Google Cloud Storage, a folder |
| F28.2 | Containers, prefixes and objects in the tree, paged rather than walked |
| F28.3 | An object's details and a preview: text, JSON, CSV, an image, a Parquet schema |
| F28.4 | A file or a whole prefix queried as a table, through DuckDB, with the studio's own grid |
| F28.5 | Upload, delete and copy behind a confirmation, refused on a read-only or production connection |
| F28.6 | The machine's own identity as credentials, or explicit keys stored encrypted |
| F28.7 | The storage extensions bundled into the image, so a private network needs no download |
| F28.8 | A storage connection attached from an Aspire app host |
| F29.1 | What the server runs on a schedule, with its history |
| F29.2 | A job enabled, disabled or started as a statement |
| F29.3 | An interactive Entra sign-in, for a person rather than a machine |
| F29.4 | Presets for the connection strings nobody remembers |
| F29.5 | What ran in the next minute, sampled |
| F29.6 | A read of the statement before it runs, which warns and never refuses |
| F29.7 | Find a value in any table |
| F29.8 | Read only the schemas somebody works in |
| F29.9 | Export formats written as text rather than as code |
| F30.1 | What is inside a JSON column, and the SELECT that flattens it |
| F30.2 | A file becomes a new table |
| F30.3 | Follow a table, with what is new tinted |
| F30.4 | What this studio has run, and whether it is getting slower |
| F30.5 | How much every table grew |
| F30.6 | What the captured minute suggests |
| F30.7 | Rules about the data rather than the schema |
| F30.8 | A failing rule reported with the health findings |
| F30.9 | Who did what through this studio |
| F30.10 | Signing in with an identity provider |
| F30.11 | A development subset that loads |
| F30.12 | The newer capabilities as MCP tools |
| F30.13 | An object shown where it lies |
| F30.14 | Save as, streamed to where it was asked for |
| F30.15 | A folder taken with you, as one zip |
| F30.16 | A file dropped where it belongs |
| F30.17 | What a table actually holds, counted |
| F30.18 | A column the values gave away |
| F30.19 | A rule and a mask made out of the numbers |
| F30.20 | Quality rules the deployment owns |
| F30.21 | Which way a rule is going |
| F30.22 | The rows kept before a statement takes all of them |
| F30.23 | An index measured rather than claimed |
| F30.24 | Notes on an object, kept by the studio |
| F30.25 | A saved query as a form, and a link that runs |
| F30.26 | An alert that links back to what it is about |
| F30.27 | The profile and the notes as MCP tools |
| F30.28 | The data tab on an engine with no SQL: a collection paged with a find, a key space paged as its keys |
| F30.29 | What the engine could not do with a query, said in the footer rather than swallowed |
| F30.30 | The theme a deployment starts in, without taking a person's own choice away |
| F30.31 | Editors for the objects a table designer never covered: views, routines, triggers, sequences, schemas, descriptions |
| F30.32 | Accounts and roles: who exists, who is in which role, what each may do, and every change as a statement first |
| F30.33 | A transaction a query tab holds open: begin, look at what it did, commit or roll back |
| F30.34 | Keep going after a failed statement, when that is what was asked for |
| F30.35 | A pivot over the result on screen, and two plans of the same statement held against each other |
| F30.36 | A file out of a binary cell and back into one, as the file it actually is |
| F30.37 | A notification when a long run finishes and nobody is watching |
| F30.38 | Timestamps read by a person, on the clock they chose, and never converted when they carry no zone |
| F30.39 | The schema drift as a script: what to run where the change has not happened yet |
| F30.40 | A dashboard: statements side by side, running themselves |
| F30.41 | What a row looked like before, where the database itself kept it |
| F30.42 | One setting, several paths: what a repository ships and what an app host wrote both count |
| F30.43 | The rest of what a deployment brings: connections, the masking baseline, dashboards, snippets, the preferences a studio starts with |

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
| P10 | Bilingual documentation site on GitHub Pages | F15.1–F15.6 |

The tool is usable after P2, replaces phpMyAdmin after P4, and covers day-to-day DataGrip
work after P6.
