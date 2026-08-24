<p align="center">
  <img src="web/public/brand/logo.svg" alt="WebDataStudio" height="90">
</p>

<p align="center">
  A database studio that runs in your browser. One container, nine engines, no install.
</p>

<p align="center">
  <a href="https://fgilde.github.io/WebDataStudio/">Documentation</a> ·
  <a href="https://fgilde.github.io/WebDataStudio/#/de/">Dokumentation (Deutsch)</a> ·
  <a href="https://github.com/fgilde/WebDataStudio/pkgs/container/webdatastudio">Container image</a>
</p>

---

## Run it

```bash
docker run -d -p 8080:8080 -v wds-data:/data \
  -e WDS_CONN_LOCAL="postgres://app:pw@db:5432/shop" \
  ghcr.io/fgilde/webdatastudio
```

Open <http://localhost:8080>. Without `WDS_USER` and `WDS_PASSWORD` there is **no login screen** —
the app opens straight into the studio.

### In .NET Aspire

```csharp
var db = builder.AddPostgres("db").AddDatabase("shop");

builder.AddContainer("studio", "ghcr.io/fgilde/webdatastudio")
       .WithHttpEndpoint(port: 8080, targetPort: 8080)
       .WithEnvironment("WDS_CONN_SHOP", db.Resource.ConnectionStringExpression)
       .WithVolume("wds-data", "/data");
```

Every connection string you can express as an environment variable is attached at startup, so a
studio for your development stack is one resource in the app host.

Or with [Nextended.Aspire.Hosting.WebDataStudio](https://www.nuget.org/packages/Nextended.Aspire.Hosting.WebDataStudio/),
which wires the databases of your stack into the studio for you:

```csharp
builder.AddPostgres("pg").AddDatabase("shop").WithWebDataStudio();
builder.AddSqlServer("sql").AddDatabase("orders").WithWebDataStudio();
```

Both databases land in one studio; a second `studioName` gives you a second studio.

## Engines

PostgreSQL · MySQL and MariaDB · Microsoft SQL Server · SQLite · Oracle · DuckDB · ClickHouse ·
MongoDB · Redis

Each driver declares what it can do, and the UI hides what an engine does not support instead of
offering a button that fails.

## What it does

- **Query editor** — Monaco with dialect-aware highlighting, schema-aware completion, formatting,
  the statement under the cursor highlighted, run selection with F5, bind parameters, snippets,
  saved queries with folders, and history that survives a restart.
- **Results** — a virtualised grid for hundreds of thousands of rows, grouping, a form view, a
  transposed view, charts, a map for geography, and a comparison between two results. Every
  per-column filter reads a small language: `^starts`, `$ends`, `~hasn't`, `>10`, `NULL`,
  `LAST MONTH`, a space for AND and a comma for OR.
- **Archives** — a result kept as a file the studio holds on to, reopened as a grid later, and
  scripted back out as inserts. NDJSON, so anything can read it.
- **Editing** — spreadsheet-style cell editing with a change-script preview before anything runs,
  foreign-key lookups, bulk updates, insert and delete, one step of undo, and generated test rows
  that respect the columns' types and foreign keys.
- **Export and import** — CSV, TSV, Excel, JSON, NDJSON, XML, YAML, Markdown, HTML, SQL inserts,
  SQL schema and Parquet, streamed rather than buffered; import from CSV, Excel, JSON and SQL with
  a column mapping; table-to-table copy across engines.
- **Schema** — a table designer, index and constraint management, view, procedure and trigger
  editing, and a migration script preview for every change. Per object: statistics, privileges,
  dependencies, its `CREATE` statement, row-level security policies, and the partitions of a
  partitioned table.
- **Following the data** — a column borrowed from the table a foreign key points at, shown next to
  the id; and a perspective panel that opens one row into everything related to it, as deep as you
  care to look.
- **Analysis** — estimated and actual execution plans with a cost heat map, an index advisor that
  writes the `CREATE INDEX` for you, a deep analyze for missing, unused and duplicate indexes,
  table statistics, slow queries and server metrics.
- **Comparison** — schema and data diffs between two connections, with a sync script in a diff
  editor.
- **Administration** — maintenance commands, sessions, databases, users and privileges, server
  logs, a dashboard that draws its numbers over half an hour rather than only the last reading, and
  backup and restore through the engines' own tools with the format and flags they offer.
- **Diagrams** — an ER diagram per schema with automatic layout, a table picker, and PNG and SVG
  export.
- **The shell** — a command palette on Ctrl+K, keyboard shortcuts with a help overlay and a new
  binding for any command, preferences that live in the workspace, dockable panels, saved layout
  presets, deep links to an object, and 21 themes.
- **For agents** — an optional assistant, and the studio itself as an MCP server with the same rules
  a person gets.

[`docs/features.md`](docs/features.md) lists every feature with its status and the engines it
applies to; a test fails the build if that list falls behind.

## Environment variables

| Variable | Meaning |
|---|---|
| `WDS_CONNECTIONS` | JSON array of connection objects, applied at startup |
| `WDS_CONN_<NAME>` | one connection as a URL, e.g. `postgres://user:pw@host:5432/db`, or a provider connection string |
| `WDS_CONN_<NAME>_ENGINE` | the engine for a `WDS_CONN_<NAME>` that holds a provider connection string instead of a URL |
| `WDS_CONN_<NAME>_READONLY`, `_GROUP`, `_COLOR` | per-connection flags for the variable of the same name |
| `WDS_USER`, `WDS_PASSWORD` | when **both** are set, a login screen guards the app; otherwise anonymous |
| `WDS_TITLE` | a name for this studio, shown in the header and the browser tab; unset shows nothing |
| `WDS_SECRET_KEY` | AES key (base64, 32 bytes) for stored connection secrets; generated into `/data/.key` if absent |
| `DB_PATH` | application SQLite database, default `/data/webdatastudio.db`; local storage only, never a network share |
| `WDS_QUERY_TIMEOUT_SECONDS` | default statement timeout, default 300 |
| `WDS_MAX_ROWS` | default fetch cap per result, default 1000 |
| `WDS_MAX_SESSIONS` | open sessions per connection, default 8 |
| `WDS_IDLE_TIMEOUT_SECONDS` | how long an unused session stays open, default 300 |
| `WDS_READONLY` | when `true`, every connection is read-only regardless of its own flag |
| `ASPNETCORE_URLS` | listen address, default `http://0.0.0.0:8080` |

Everything optional is off until it is configured, one group at a time:

| Variables | What they turn on |
|---|---|
| `WDS_USERS` | several accounts, each with a role and the connections it may see |
| `WDS_MASK_EXTRA`, `WDS_MASK_NEVER`, `WDS_MASK_DEFAULT` | which columns are masked before they leave the server |
| `WDS_ASSIST_ENDPOINT`, `WDS_ASSIST_KEY`, `WDS_ASSIST_MODEL`, `WDS_ASSIST_TOOLS` | the optional assistant |
| `WDS_MCP_ENABLED`, `WDS_MCP_PATH`, `WDS_MCP_KEY`, `WDS_MCP_ALLOW_WRITE`, `WDS_MCP_TOOLS` | the studio as an MCP server |
| `WDS_SHARE_ENABLED`, `WDS_SHARE_PUBLIC`, `WDS_SHARE_TTL_HOURS`, `WDS_SHARE_MAX_ROWS` | a result shared as a link |
| `WDS_ARCHIVE_DIR`, `WDS_ARCHIVE_MAX_ROWS` | where kept results are written, and how big one gets |
| `WDS_SCHEDULE_FILE`, `WDS_SCHEDULE_OUTPUT_DIR` | queries the studio runs on a schedule and writes as files |
| `WDS_SAVED_QUERIES_DIR`, `WDS_SEED_SQL` | queries and data that ship with the stack |
| `WDS_SCHEMA_SNAPSHOT_DIR` | schema snapshots on start, and the drift since the last one |
| `WDS_ALERT_WEBHOOK`, `WDS_ALERT_INTERVAL_MINUTES`, `WDS_ALERT_MIN_SEVERITY`, `WDS_ALERT_CONNECTIONS` | new health findings posted to a webhook |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME` | the studio's own traces and metrics |

[`docs/guide/environment.md`](docs/guide/environment.md) is the complete table, with what each one
does and what happens when it is absent.

`WDS_CONNECTIONS` entry shape:

```json
[{
  "name": "prod-pg",
  "engine": "postgresql",
  "connectionString": "Host=db;Port=5432;Database=shop;Username=app;Password=secret",
  "readOnly": true,
  "color": "#e03131",
  "group": "Production"
}]
```

URL schemes map to engines: `postgres`, `postgresql`, `mysql`, `mariadb`, `sqlserver`, `mssql`,
`sqlite`, `oracle`, `duckdb`, `clickhouse`, `mongodb`, `redis`.

A `WDS_CONN_<NAME>` may also carry the provider's own connection string — the form an orchestrator
already has. Say which engine it is:

```bash
WDS_CONN_SHOP="Host=db;Port=5432;Username=app;Password=pw;Database=shop"
WDS_CONN_SHOP_ENGINE=postgresql
```

Connections defined in the environment are re-read on every start, are read-only in the UI, and
carry a badge. Connections added in the UI live in `/data` with their passwords encrypted at rest.

## Desktop

Prefer a local binary to a container? Each release ships a self-contained build for Windows, macOS
and Linux. It starts the same server and opens the studio in a window of its own — no address bar, an
icon in the task bar — using the platform's web view (WebView2, WKWebView, WebKitGTK), and falling
back to an installed Chromium in app mode and then to a browser tab if that is not available:

```bash
./webdatastudio                        # http://localhost:8080, in its own window
WDS_APP_WINDOW=false ./webdatastudio   # a normal browser tab instead
```

Downloads are on the [releases page](https://github.com/fgilde/WebDataStudio/releases).

A studio that is already running can also be installed straight from the browser — **Install
WebDataStudio** in Chrome's or Edge's address bar — which gives the same window without downloading
anything. Nothing is cached either way: the studio reads live databases.

## Develop

```bash
# API on :5000
ASPNETCORE_URLS=http://localhost:5000 DB_PATH=/tmp/wds.db dotnet run --project src/WebDataStudio.Server

# SPA on :5173, proxying /api to :5000
cd web && npm install && npm run dev
```

Tests:

```bash
dotnet test                       # server, including live databases through Testcontainers
cd web && npx vitest run          # SPA units
cd web && npm run smoke           # browser check against a running server
```

## Licence

MIT.
