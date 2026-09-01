# Feature coverage

Every feature id from the design spec, with what it does, whether it is implemented, which
engines it applies to, and where to find it. `FeatureCoverageTests` reads this file and fails
the build if an id from the spec is missing here, so the list cannot quietly fall behind.

Status is one of `done`, `partial: <what is missing>` or `not-supported: <engines>`.

| Id | Feature | Status | Engines | Where |
|----|---------|--------|---------|-------|
| F1.1 | Environment connections (`WDS_CONNECTIONS` array and `WDS_CONN_<NAME>` URLs), read-only badge | done | all | Connections page, explorer |
| F1.2 | UI CRUD with a per-engine form; pasting a connection string detects the engine and fills the form | done | all | Connections page, explorer |
| F1.3 | Test connection before saving | done | all | Connections page, explorer |
| F1.4 | SSH tunnel, SSL/TLS options, client certificates | partial: TLS mode is written into the connection string; client certificate files are referenced by path, not uploaded | all | Connection form, SSH and TLS sections |
| F1.5 | Connection folders/groups and colour marking (production = red) | done | all | Connection form, explorer groups |
| F1.6 | Per-connection read-only flag, enforced in the driver | done | all | Connections page, explorer |
| F1.7 | Connection pooling, auto-reconnect, idle timeout | done | all | Connection form, explorer groups |
| F1.8 | Import/export of connection definitions as JSON, without secrets | done | all | Connection form, explorer groups |
| F2.1 | Lazy tree: server, database, schema, tables, views, materialised views, procedures, functions, triggers, sequences, indexes, constraints, types, users | partial: users live in the administration panel rather than in the tree; a table expands into its columns, indexes, keys and triggers | all | Explorer |
| F2.2 | Tree filter and fuzzy search, "go to object" (Ctrl+Shift+O) | done | all | Explorer, structure panel |
| F2.3 | Context menu: open data, show DDL, rename, drop, truncate, generate SELECT/INSERT/UPDATE/DELETE/CREATE script | done | all | Explorer, structure panel |
| F2.4 | Object detail panel: columns, indexes, foreign keys, triggers, size, row count, comments | done | all | Explorer, structure panel |
| F2.5 | Dependencies in both directions | done | all | Explorer, structure panel |
| F2.6 | Several connections open at once | done | all | Explorer, structure panel |
| F2.7 | A connection's health in the tree: reachable, how long it took, why not | done | all | Explorer |
| F2.8 | PostgreSQL LISTEN/NOTIFY: watch the channels an application talks on, and send one | done | postgresql | Tools → Notifications |
| F2.9 | A data dictionary: every table, column, key and studio note as one Markdown document | done | all | Explorer context menu |
| F3.1 | Dialect-aware SQL highlighting | done | all | Query tab, saved queries, query builder |
| F3.2 | Schema-aware completion: tables, columns, aliases, functions, keywords | done | all | Query tab, saved queries, query builder |
| F3.3 | Execute selection (F5 / Ctrl+Enter); with no selection, execute the statement under the cursor | done | all | Query tab, saved queries, query builder |
| F3.4 | Statement boundary detection with the active statement visibly highlighted | done | all | Query tab, saved queries, query builder |
| F3.5 | Dialect-aware formatter | done | all | Query tab, saved queries, query builder |
| F3.6 | Inline error highlighting, server errors mapped to line and column | done | all | Query tab, saved queries, query builder |
| F3.7 | Go-to-definition on table names, hover shows columns | done | all | Query tab, saved queries, query builder |
| F3.8 | Query parameters (`:name`, `@name`) with an input dialog | done | all | Query tab, saved queries, query builder |
| F3.9 | Snippets and user-defined live templates | done | all | Query tab, saved queries, query builder |
| F3.10 | Persisted, searchable, restorable query history | done | all | Query tab, saved queries, query builder |
| F3.11 | Saved queries and bookmarks with folders | done | all | Query tab, saved queries, query builder |
| F3.12 | Multi-cursor, find and replace, regex | done | all | Query tab, saved queries, query builder |
| F3.13 | Tabs survive reload and container restart | done | all | Query tab, saved queries, query builder |
| F3.14 | Visual query designer / query-by-example | done | all | Query tab, saved queries, query builder |
| F4.1 | Streaming results rendered while the query is still running | done | all | Query tab, result area |
| F4.2 | Cancel a running query | done | all | Query tab, result area |
| F4.3 | Multiple statements in sequence, one result tab per statement | done | all | Query tab, result area |
| F4.4 | Messages tab: notices, warnings, `PRINT`, `RAISE NOTICE`, affected rows | done | all | Query tab, result area |
| F4.5 | Explicit transaction control: auto-commit toggle, commit and rollback actions | partial: one transaction per script through the toggle; there is no pinned session holding an open transaction across requests | all | Query tab toolbar |
| F4.6 | Timing per statement and total | done | all | Query tab, result area |
| F4.7 | Session/process list, kill foreign sessions | done | postgresql, mysql, sqlserver, oracle, mongodb, redis, clickhouse | Administration, sessions tab |
| F4.8 | Configurable query timeout and row cap, overridable per run | done | all | Query tab, result area |
| F5.1 | Virtualised grid handling hundreds of thousands of rows | done | all | Result area |
| F5.2 | Sort, per-column filter, full-text search within the result | done | all | Result area |
| F5.3 | Hide, reorder, pin and resize columns with persisted widths | done | all | Result area |
| F5.4 | Cell value viewer: text, JSON, XML, hex, image, BLOB download | done | all | Result area |
| F5.5 | NULL visually distinct from an empty string | done | all | Result area |
| F5.6 | Selection aggregate (sum, average, count) in the status bar | done | all | Result area |
| F5.7 | Form view for rows with many columns | done | all | Result area |
| F5.8 | Master-detail over foreign keys; clicking a foreign key jumps to the target row | done | all | Result area |
| F5.9 | Grid grouping | done | all | Result area |
| F5.10 | Compare two result sets | done | all | Result area |
| F5.11 | Charts from a result (bar, line, pie) | done | all | Result area |
| F5.12 | Transposed view | done | all | Result area |
| F6.1 | Inline editing in the grid, spreadsheet-like, batched | done | all | Data tab |
| F6.2 | Insert, delete and duplicate rows | done | all | Data tab |
| F6.3 | Change-script preview before applying — always, not optional | done | all | Data tab |
| F6.4 | Tables without a primary key: a unique index, then the engine's own row address (`ctid`, `ROWID`, `rowid`); blocked with a clear reason where there is neither | done | all | Data tab |
| F6.5 | Foreign-key lookup dropdown while editing | done | all | Data tab |
| F6.6 | Bulk update over a selection via macro or expression | done | all | Data tab |
| F6.7 | Paste rows from the clipboard as inserts — tab or comma, header by name, quoted cells, blank means null | done | all | Data tab |
| F6.8 | Keep a result as a new table, in this connection or another one, with the `CREATE TABLE` shown first | done | all | Result toolbar |
| F7.1 | Export to CSV, TSV, Excel (xlsx), JSON, NDJSON, XML, YAML, Markdown, HTML, SQL INSERTs, SQL CREATE+INSERT, Parquet | done | all | Export and import dialogs |
| F7.2 | Export scope: selection, current page, whole result, whole table, whole schema | partial: the dialog exports the result, a table or a whole schema; a selection goes through the copy actions | all | Export dialog, result copy menu |
| F7.3 | Streaming export with no server-side materialisation | done | all | Export and import dialogs |
| F7.4 | Export options: delimiter, encoding, quoting, header row, NULL representation, date format | done | all | Export and import dialogs |
| F7.5 | Copy as CSV / JSON / SQL IN-list / Markdown table to the clipboard | done | all | Export and import dialogs |
| F7.6 | Import from CSV, Excel, JSON and SQL with column mapping and preview | done | all | Export and import dialogs |
| F7.7 | Table-to-table copy, including across engines | done | all | Export and import dialogs |
| F8.1 | Show and edit the DDL of any object | done | postgresql, mysql, sqlserver, sqlite | Table designer, explorer context menu |
| F8.2 | Table designer: columns, types, nullability, defaults, identity, comments | done | postgresql, mysql, sqlserver, sqlite | Table designer, explorer context menu |
| F8.3 | Index management: create, alter, drop; unique, partial and include columns where supported | done | postgresql, mysql, sqlserver, sqlite | Table designer, explorer context menu |
| F8.4 | Constraints: primary key, foreign key, unique, check | done | postgresql, mysql, sqlserver, sqlite | Table designer, explorer context menu |
| F8.5 | Create and alter views, procedures, functions and triggers | done | postgresql, mysql, sqlserver, sqlite | Table designer, explorer context menu |
| F8.6 | Migration script preview for every change; execution only after confirmation | done | postgresql, mysql, sqlserver, sqlite | Table designer, explorer context menu |
| F8.7 | Rename an object with a dependency warning | done | postgresql, mysql, sqlserver, sqlite | Table designer, explorer context menu |
| F9.1 | Estimated plan and actual plan | done | all | Plan panel, health panel, administration metrics |
| F9.2 | Plan as a tree plus a graphical view with a cost heat map | done | all | Plan panel, health panel, administration metrics |
| F9.3 | Expensive nodes highlighted: sequential scan on a large table, missing index, spill to disk | done | all | Plan panel, health panel, administration metrics |
| F9.4 | Index advisor producing concrete `CREATE INDEX` statements with a rationale | done | all | Plan panel, health panel, administration metrics |
| F9.5 | Deep analyze: missing, unused and duplicate indexes, table bloat, stale statistics, unindexed foreign keys | done | all | Plan panel, health panel, administration metrics |
| F9.6 | Table statistics: size, row count, index size, last vacuum/analyze | done | all | Plan panel, health panel, administration metrics |
| F9.7 | Slow query view where the engine provides one (pg_stat_statements, Query Store, performance_schema) | done | postgresql, mysql, sqlserver | Administration, slow queries tab |
| F9.8 | Server metrics: connections, cache hit ratio, locks, blocking chains | done | all | Plan panel, health panel, administration metrics |
| F10.1 | Schema diff between two connections with a generated sync script | done | all | Compare panel |
| F10.2 | Data diff between two tables with a generated sync script | done | all | Compare panel |
| F10.3 | Diff rendered in the Monaco diff editor | done | all | Compare panel |
| F11.1 | Backup and restore where the engine supports it (pg_dump, mysqldump, `BACKUP DATABASE`, SQLite `.backup`) | done | postgresql, mysql, mongodb, redis, sqlite, sqlserver | Administration, backup tab |
| F11.2 | User and privilege management | done | postgresql, mysql, sqlserver, oracle | Administration, users tab |
| F11.3 | Capability-gated system commands: VACUUM, ANALYZE, REINDEX, OPTIMIZE, CHECKDB, FLUSH, configuration variables | done | all | Administration, maintenance tab |
| F11.4 | Server log viewer where reachable | done | postgresql, mysql, sqlserver | Administration, log tab |
| F11.5 | Create and drop databases | done | postgresql, mysql, sqlserver, clickhouse, mongodb | Administration, databases tab |
| F12.1 | ER diagram per schema using `@xyflow/react` and `dagre` | done | all | Diagram panel |
| F12.2 | Table selection, auto layout, export as PNG and SVG | done | all | Diagram panel |
| F12.3 | Clicking a table in the diagram opens its data or structure | done | all | Diagram panel |
| F12.4 | Backups on a schedule, kept and pruned per job | done | postgresql, mysql, mongodb, redis | Deployment setting |
| F12.5 | Seed a connection from another one at start, leaving existing tables alone | done | all | Deployment setting |
| F13.1 | Command palette (Ctrl+K) covering every action | done | all | Shell, command palette, theme drawer |
| F13.2 | Keyboard shortcuts throughout with a shortcut help overlay | done | all | Shell, command palette, theme drawer |
| F13.3 | The 21 AspireUI themes and the theme drawer | done | all | Shell, command palette, theme drawer |
| F13.4 | Save, apply and reset layout presets, `Ctrl+L` plus a digit per preset | done | all | Shell, command palette, theme drawer |
| F13.5 | Deep links to an object or a query | done | all | Shell, command palette, theme drawer |
| F13.6 | Toasts for long-running jobs | done | all | Shell, command palette, theme drawer |
| F13.7 | The explorer is a dock panel: drag, split or close it, `Ctrl+B` brings it back | done | all | Shell |
| F13.8 | The running build shown bottom right, with its commit and build time | done | all | Shell |
| F13.9 | The query tab's split is draggable, and the editor's text zooms with Ctrl and the wheel | done | all | Query tab |
| F13.10 | Closing many tabs at once leaves the studio's own panels standing | done | all | Tab context menu |
| F14.1 | MongoDB: collection browser, JSON editor, aggregation pipeline, index management | partial: the command console runs find, aggregate and index commands; there is no separate pipeline builder | mongodb | Query tab on a MongoDB connection |
| F14.2 | Redis: key tree, type-specific editors, TTL, command console | done | redis | Redis panel, query tab |
| F14.3 | Result renderer as a JSON tree, switchable to a table for flat documents | done | mongodb | Result area, document view |
| F14.7 | Look at a file rather than download it: spreadsheets, documents and archives through an on-demand viewer | done | storage, all | Bucket preview, explorer menu, cell viewer |
| F15.1 | GitHub Pages site served from `/docs`, docsify like AspireUI, same visual language | done | all | docs/ site |
| F15.2 | Every page in English and German, switchable in the sidebar; docsify i18n through a `/docs/de/` tree | done | all | docs/ site |
| F15.3 | Content: getting started, environment variables, connections, query editor, results and export, schema editing, analysis, administration, engine capability matrix, shortcuts | done | all | docs/ site |
| F15.4 | Screenshots per major feature, captured in a dark and a light theme | done | all | docs/ site |
| F15.5 | README links to the site in both languages; the site links back to the repository and the GHCR image | done | all | docs/ site |
| F15.6 | A link check in CI so a renamed page cannot silently break the sidebar | done | all | docs/ site |
| F16.1 | Entra authentication for Azure SQL: `Authentication=Active Directory Default` with the container's managed identity | done | sqlserver | Connections, deployed studio |
| F16.2 | A data directory that is slow or unusable degrades to environment-only connections, reported in /api/health and in the window | done | all | Shell, /api/health |
| F17.1 | Query builder on a canvas: tables as cards, joins as lines, a checkbox per column | done | all | Query builder |
| F17.2 | Joins proposed from the foreign keys, in either direction, composite keys included | done | all | Query builder |
| F17.3 | Live SQL and the first 50 rows of the query while it is being built | done | all | Query builder |
| F17.4 | Aggregates with automatic GROUP BY, HAVING, DISTINCT, per-column aliases | done | all | Query builder |
| F17.5 | A generated statement carries its model, so the query reopens in the builder | done | all | Query builder, command palette |
| F23.1 | The data tab's column menu: a filter that takes the focus, debounced against the server, and hidden columns with an indicator | done | all | Data tab |
| F23.2 | The explorer box searches tables and views by subsequence, ranked, with the schema as context | done | all | Explorer |
| F23.3 | Tab context menu: close, close others, close to the right, close all, pin, maximise, and a panel in its own window | done | all | Shell, tab strip |
| F19.1 | Key browser: SCAN with a cursor, pattern and type filter on the server, size and TTL per key | done | redis | Redis panel, keys tab |
| F19.2 | An editor per value type — string with format detection, hash, list, set, sorted set, stream | done | redis | Redis panel, keys tab |
| F19.3 | Every write previewed as the Redis commands it will run, TTL set or removed per key | done | redis | Redis panel, keys tab |
| F19.4 | Delete or expire by pattern, applied to the set that was previewed | done | redis | Redis panel, keys tab |
| F19.5 | Keyspace analysis: memory by prefix, types, largest keys, what expires soonest | done | redis | Redis panel, analysis tab |
| F19.6 | Live pub/sub with publishing, and stream consumer groups with their pending entries | done | redis | Redis panel, pub/sub tab |
| F19.7 | Slow log and connected clients | done | redis | Redis panel, slow log tab |
| F19.8 | Console completion and hover from the server's own COMMAND DOCS, and a cluster view that also answers for a standalone server | done | redis | Query tab, Redis panel, cluster tab |
| F18.1 | Overview tab: connections, cache hit, waiting and running sessions, longest statement, with a short history per tile | done | postgresql, mysql, sqlserver, oracle | Administration, overview tab |
| F18.2 | Progress of running work where the engine reports it, and every long statement with its age | done | postgresql, sqlserver, mysql, oracle | Administration, overview tab |
| F18.3 | Blocking chains as a tree, with kill at the root | done | postgresql, sqlserver, mysql | Administration, overview tab |
| F18.4 | Every health finding carries the statement that fixes it, applied through the migration preview | done | postgresql, mysql, sqlserver, sqlite | Health panel |
| F18.5 | Replication state with lag | done | postgresql, mysql, sqlserver | Administration, replication tab |
| F18.6 | Database sizes as a treemap | done | all | Administration, databases tab |
| F20.1 | Columns that look like secrets masked on the server, revealed on purpose, per-connection overrides, exports refused unmasked on production | done | all | Data tab, query results, export |
| F20.2 | Undo for an applied data change, the inverse shown as a script before it runs | done | all | Data tab |
| F20.3 | Several studio accounts with roles (admin, editor, viewer) and their own connections | done | all | Login, administration, studio users tab |
| F20.4 | The mask policy can come from the deployment: extra columns, exempt columns, or the heuristic off entirely | done | all | Environment, data tab |
| F21.1 | Federated query across connections, staged in DuckDB, with a row cap per source and the staging shown first | done | all | Federated panel |
| F21.2 | Watch a query on an interval, changed cells highlighted, one run at a time | done | all | Query tab |
| F21.3 | Generated test rows with a strategy per column, foreign keys pointing at existing rows, seeded | done | all | Data tab, generate dialog |
| F21.4 | Notebooks: SQL and prose cells with their results, saved in the workspace, Markdown in and out | done | all | Notebook panel |
| F22.1 | Optional explain and draft-SQL against an OpenAI-compatible endpoint; absent unless configured, sends no row of data, executes nothing | done | all | Query tab |
| F22.2 | MCP server over JSON-RPC/HTTP: list, describe, browse, read-only query, and a previewed write behind a flag; masking and read-only enforced for agents too | done | all | MCP endpoint, header dialog |
| F22.3 | The assistant answers from the database through the MCP tools, naming the ones it used | done | all | Query tab, assistant dialog |
| F22.4 | A chat in the corner with sessions that survive a reload, the MCP tools where they exist, and one click from a suggested statement to the editor | done | all | Chat panel |
| F22.5 | MCP tools for the plan, the health report, server activity and a Redis value | done | all | MCP endpoint |
| F22.6 | The MCP endpoint can be narrowed to named tools, enforced on the call as well as the listing | done | all | MCP endpoint |
| F24.1 | New health findings posted to a webhook on a timer, deduplicated, retried on failure | done | postgresql, mysql, sqlserver, sqlite, oracle | Environment |
| F24.2 | Schema snapshots on start, with the drift since the last one per table, on an endpoint, in the log and in an alert | done | all | Environment, schema panel |
| F24.3 | A folder of .sql files imported as saved queries at start, idempotently, with the connection and folder in comments | done | all | Saved panel |
| F24.4 | A seed script run once per connection, never on a read-only or production one | done | all | Environment |
| F24.5 | Scheduled read-only queries exported to files, on an interval or daily, with the last run per job on an endpoint | done | all | Environment |
| F24.6 | A result kept as a snapshot behind a random link, masked before storage, expiring, public only on purpose | done | all | Result area, share page |
| F24.7 | The studio's own traces and metrics over OTLP: a span per run and per tool call, counters for statements, rows and tool calls | done | all | Environment |
| F25.1 | Statistics per object: size, rows, dead rows, last vacuum and analyze, and every index with its size and scan count | done | postgresql, mysql, sqlserver, oracle | Structure panel, statistics tab |
| F25.2 | Privileges per object, with GRANT and REVOKE built as statements that go through the migration preview | done | postgresql, mysql, sqlserver, oracle | Structure panel, privileges tab |
| F25.3 | Dependencies per object — what breaks if this changes, and what this needs | done | all | Structure panel, dependencies tab |
| F25.4 | The object as a CREATE statement, to copy or open in a query tab | done | all | Structure panel, SQL tab |
| F25.5 | Plan findings for a spilled sort and a nested loop carrying many rows | done | postgresql, mysql, sqlserver, oracle | Plan panel |
| F26.1 | Extensions, roles, tablespaces, publications, subscriptions and types in the tree, next to the schemas | done | postgresql | Explorer |
| F26.2 | GRANT or REVOKE for every table in a schema in one script, optionally for tables created later | done | postgresql, mysql, sqlserver, oracle | Explorer, schema menu |
| F26.3 | REFRESH MATERIALIZED VIEW, plain or CONCURRENTLY, refused on anything that is not one | done | postgresql, oracle | Explorer, materialised view menu |
| F26.4 | The dashboard's numbers as lines over five, fifteen or thirty minutes | done | all | Admin panel, overview |
| F26.5 | Backup format, compression, no-owner and clean, with the bytes counted as they arrive | partial: format and its flags are pg_dump's; the other tools refuse them rather than ignore them | postgresql, mysql, mongodb, redis, sqlite, sqlserver | Admin panel, backup |
| F26.6 | A function's source, parameters and a run inside a rolled-back transaction, with its notices and timing | partial: not a stepping debugger — no breakpoints and no variable inspection | postgresql | Structure panel, inspect tab |
| F26.7 | Row-level security and its policies, created and dropped as statements | done | postgresql | Structure panel, policies tab |
| F26.8 | A partitioned table's pieces with their bounds and sizes, and ATTACH or DETACH as statements | done | postgresql | Structure panel, partitions tab |
| F26.9 | Preferences in the workspace: page size, snapshots, and a new binding for any command | done | all | Preferences dialog |
| F26.10 | A history entry that keeps its result, reopened as a grid | done | all | History panel |
| F27.1 | A filter language in every column box: `^starts`, `$ends`, `+has`, `~hasn't`, `=`, `!=`, `>`, `<=`, `NULL`, `EMPTY`, `TODAY`, `LAST MONTH`, `2026-08`, quoted values, space for AND, comma for OR | done | all | Data tab, result grid |
| F27.2 | The distinct values of a column with their counts, as checkboxes that write the filter | done | all | Data tab, column menu |
| F27.3 | A column borrowed from the table a foreign key points at, joined server-side and read-only | done | all | Data tab, column menu |
| F27.4 | A nested view over related rows: what this row points at, what points back, as deep as it is opened | partial: single-column keys only, and one page per level | all | Perspective panel |
| F27.5 | Results kept as NDJSON files on the studio's disk, listed, reopened as a grid, and scripted back as INSERTs | done | all | Archive panel, result area, explorer |
| F27.6 | GeoJSON, WKT or a latitude/longitude pair drawn to scale | partial: no basemap — a container has no tile server, and the studio will not reach out to one on its own | all | Result area, map view |
| F27.7 | `EXISTS` and `NOT EXISTS` over a table that is not in the query | done | all | Query builder |
| F27.8 | A pager that says which rows are on screen — `1–200 of 12,345` — with first, last and a typed page number, the page size beside it, and an exact count on request where the total is the catalogue's estimate or a filter narrows the result | done | all | Data tab |
| F28.1 | Object storage as a connection: S3-compatible, Azure Blob, Google Cloud Storage, a folder | done | storage | Connections, `WDS_CONN_*`, Add a bucket |
| F28.2 | Containers, prefixes and objects in the tree, paged rather than walked | done | storage | Explorer |
| F28.3 | An object's details and a preview: text, JSON, CSV, an image, a Parquet schema | done | storage | Structure panel |
| F28.4 | A file or a whole prefix queried as a table, through DuckDB, with the studio's own grid | done | storage | Data tab, query tab |
| F28.5 | Upload, delete and copy behind a confirmation, refused on a read-only or production connection | done | storage | Explorer, object menu |
| F28.6 | The machine's own identity as credentials, or explicit keys stored encrypted | done | storage | Connections |
| F28.7 | The storage extensions bundled into the image, so a private network needs no download | done | storage | Image |
| F28.8 | A storage connection attached from an Aspire app host | done | storage | Nextended.Aspire.Hosting.WebDataStudio |
| F29.1 | What the server runs on a schedule: SQL Server Agent jobs, pg_cron entries, MySQL events, with their history | done | sqlserver, postgresql, mysql | Admin panel, jobs tab |
| F29.2 | Enabling, disabling or starting a job as a statement rather than a click | done | sqlserver, postgresql, mysql | Admin panel, jobs tab |
| F29.3 | An interactive Entra sign-in for Azure SQL, Synapse and Fabric: a device code the person enters elsewhere | done | sqlserver | Connections |
| F29.4 | Presets for the connection strings nobody remembers — Azure SQL, Synapse, Fabric, the Azure database services, a bucket | done | all | Connection form |
| F29.5 | What ran in the next minute, sampled once a second and grouped by statement | done | sqlserver, postgresql, mysql | Admin panel, capture tab |
| F29.6 | A read of the statement before it runs: no WHERE, an always-true WHERE, = NULL, TRUNCATE, DROP, an accidental cross product | done | all | Query editor |
| F29.7 | Find a value in any table, server-side and type-aware | done | postgresql, mysql, sqlserver, sqlite, oracle, duckdb, clickhouse | Find data panel |
| F29.8 | Read only the schemas somebody works in, from the environment or per studio | done | postgresql, mysql, sqlserver, oracle, clickhouse, duckdb | Explorer, connection properties |
| F29.9 | Export formats written as text with placeholders rather than as code | done | all | Export dialog, templates |
| F30.1 | What is inside a JSON or JSONB column: which paths exist, how often, with which types — and the SELECT that flattens them into columns | done | postgresql, mysql, sqlserver, sqlite, duckdb, clickhouse, oracle | Data tab, column menu |
| F30.2 | A file becomes a new table: an upload or an object in a bucket, described, previewed and created before anything is loaded | done | postgresql, mysql, sqlserver, sqlite, duckdb, clickhouse | Explorer, New table from file |
| F30.3 | Follow a table: the page re-read on a timer, ordered by a key column, with the rows that are new since the last read tinted | done | all | Data tab |
| F30.4 | What this studio has run, grouped by statement shape: how often, how long, and whether it is getting slower | done | all | Query area, statement statistics |
| F30.5 | How much every table grew: sizes sampled whenever somebody looks, the biggest absolute change first, with a per-day rate | done | postgresql, mysql, sqlserver, clickhouse | Admin panel, databases tab |
| F30.6 | What the captured minute suggests: the slowest statements read by the index advisor, aggregated per table | done | sqlserver, postgresql, mysql | Admin panel, capture tab |
| F30.7 | Rules about the data rather than the schema: has a value, no duplicates, in a range, points at a row that exists, is recent, or a condition of one's own — each one counting the rows that break it | done | all | Admin panel, data quality tab |
| F30.8 | A failing quality rule reported with the health findings, so a rule written once is watched from then on | done | all | Health panel, alert webhook |
| F30.9 | Who did what through this studio: one line per request that changed something or took data out, with who asked, against which connection and what came of it | done | all | Admin panel, audit tab, `WDS_AUDIT` |
| F30.10 | Signing in to the studio with an identity provider — Entra, Keycloak, Auth0, Okta — with the studio's roles mapped from the groups it sends | done | all | Login screen, `WDS_OIDC_*` |
| F30.11 | A development subset: rows from one table, the rows they point at, what is about people replaced, written as one loadable SQL script | done | postgresql, mysql, sqlserver, sqlite, oracle, clickhouse | Explorer, development subset |
| F30.12 | The newer capabilities as MCP tools: find a value, a document column's shape, growth, what this studio ran, a statement read before it runs, and the data quality rules | done | all | MCP endpoint, assistant |
| F30.13 | An object shown where it lies: an image, a PDF, a video, a recording, and a document indented rather than left on one line | done | storage | Structure panel |
| F30.14 | Save as… — the person picks the folder and the name, and the file is streamed into it rather than through memory | done | storage | Object menu, structure panel |
| F30.15 | A whole prefix downloaded as one zip, streamed, with whatever stopped the walk written into the archive itself | done | storage | Explorer, folder menu |
| F30.16 | A file dragged onto the tree: into a bucket folder as an upload, into a table as rows, into a schema as a new table | done | all | Explorer |
| F30.17 | What a table actually holds, counted in one statement: rows, empty values, distinct values, smallest and largest, per column | done | all | Structure panel, profile tab |
| F30.18 | Which columns look like they hold something personal, read from a sample of the values rather than from the column's name | done | all | Profile tab |
| F30.19 | A rule made out of what the numbers say is true today, and a column masked because its values gave it away | done | all | Profile tab |
| F30.20 | Data quality rules the deployment owns, as JSON in the repository: they run, they report, and the studio cannot change them | done | all | `WDS_QUALITY_FILE`, data quality tab |
| F30.21 | Which way a rule is going: every run kept as a measurement, and the direction rather than a mean | done | all | Data quality tab |
| F30.22 | The rows kept as an archive before a statement that takes all of them, so a DELETE with no WHERE has a way back | done | all | Query editor, `WDS_SAFETY_NET` |
| F30.23 | A suggested index measured rather than claimed: created, the plan asked again, and dropped | done | postgresql, mysql, sqlserver, sqlite, oracle, clickhouse | Plan panel, findings |
| F30.24 | Notes on any object — a name, a date and a sentence — kept in the studio rather than needing a DDL right and a migration | done | all | Structure panel, notes tab |
| F30.25 | A saved query as a form: the bind parameters as boxes, the values in the link, and the answer as a CSV | done | postgresql, mysql, sqlserver, sqlite, oracle, duckdb | Reports page |
| F30.26 | Every alert carrying the way back to what it is about, read out of the statement that would fix it | done | all | Alert webhook, `WDS_PUBLIC_URL` |
| F30.27 | The profile and the notes as MCP tools, so an agent can read what a table holds and what people wrote about it | done | all | MCP endpoint |
| F30.28 | The data tab on an engine that has no SQL: a MongoDB collection read with `find().sort().skip().limit()` and the studio's filter language translated into it, a Redis database or prefix read as the keys it holds, a Redis key read as the table its type makes | done | mongodb, redis | Data tab, explorer, MCP `browse_rows` |
| F30.29 | What the engine could not do with the query it was given — a filter with no translation, a sort a key space has no order for, a key scan that stopped at its cap — written into the footer rather than swallowed | done | mongodb, redis | Data tab footer |
| F30.30 | The theme a deployment comes up in (`WDS_THEME`, `WithTheme` in Aspire), as a starting point rather than a lock: a person's own choice wins and is never overwritten | done | all | Theme menu, `WDS_THEME` |
| F30.31 | The objects a table designer never covered, written through the same preview: a view's SELECT, a routine's or trigger's source, a sequence including the restart after an import, schemas, the description the database itself keeps, and a drop that lists what depends on it first | done | postgresql, mysql, sqlserver, sqlite | Explorer menus, object editor |
| F30.32 | Accounts and roles as one list — who may sign in, who is a superuser, who is in which role, and what each was granted directly — with create, password, sign-in on and off, membership, grant, revoke and drop, each shown as its statement before it runs | done | postgresql, mysql, sqlserver, oracle | Administration, accounts tab |
| F30.33 | A transaction a query tab holds open across statements: begin, see what they did while nobody else can, then commit or roll back — swept by the server when nobody comes back | done | postgresql, mysql, sqlserver, sqlite, oracle, duckdb | Query tab, `WDS_TRANSACTION_IDLE_SECONDS` |
| F30.34 | Keeping going after a statement fails, asked for rather than assumed, with each failure reported where it happened | done | all | Query tab |
| F30.35 | A pivot over the rows on screen — one column down the side, another across the top — and the plan of a statement held against the plan of its previous run | done | all | Result views, plan panel |
| F30.36 | A binary cell as the file it holds: saved with the extension its first bytes say it has, and replaced from disk through the same preview, written as the engine's own binary literal | done | postgresql, mysql, sqlserver, sqlite, oracle | Data tab, cell viewer |
| F30.37 | A notification when a run that took longer than the preference finishes while you are looking at something else | done | all | Query tab, preferences |
| F30.38 | Timestamps shown as a person reads them, on the clock the preference names, with the raw value on hover — and never converted where the column keeps no zone, which the header says | done | all | Result grid, data tab, preferences |
| F30.39 | The drift since the last schema snapshot as the statements that would carry another database from there to here — built from the live schema, opened in a query tab, with a type change left to a person as a comment | done | postgresql, mysql, sqlserver, sqlite | Administration, schema drift |
| F30.40 | A dashboard of statements side by side — a number, a table or a bar per row — running themselves on an interval, kept in the workspace, each tile through the same endpoint, cap, masking and audit line as a query tab | done | all | Tools, dashboard |
| F30.41 | The versions of one row as the database itself kept them — SQL Server's system-versioned tables, MariaDB's system versioning, Oracle's flashback — with what changed between them, and nothing invented where an engine keeps none | done | sqlserver, mysql (MariaDB), oracle | Data tab, row history |
| F30.42 | Saved queries, export templates, quality rules and seed scripts from a folder **and** from the app host at once: each setting takes a list of paths, and the Aspire package writes its own files into the container | done | all | `WDS_SAVED_QUERIES_DIR`, `WDS_EXPORT_TEMPLATES_DIR`, `WDS_QUALITY_FILE`, `WDS_SEED_SQL` |
| F30.43 | The rest of what a deployment can bring with it, as a file or from the app host: connections that are not resources, the masking baseline, dashboards it owns (shown, not editable), editor snippets for everybody, and the preferences a studio starts with | done | all | `WDS_CONNECTIONS_FILE`, `WDS_MASK_FILE`, `WDS_DASHBOARD_FILE`, `WDS_SNIPPETS_FILE`, `WDS_PREFERENCES_FILE` |
