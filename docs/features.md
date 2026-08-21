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
| F6.4 | Tables without a primary key: editing blocked with a clear reason | done | all | Data tab |
| F6.5 | Foreign-key lookup dropdown while editing | done | all | Data tab |
| F6.6 | Bulk update over a selection via macro or expression | done | all | Data tab |
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
| F13.1 | Command palette (Ctrl+K) covering every action | done | all | Shell, command palette, theme drawer |
| F13.2 | Keyboard shortcuts throughout with a shortcut help overlay | done | all | Shell, command palette, theme drawer |
| F13.3 | The 21 AspireUI themes and the theme drawer | done | all | Shell, command palette, theme drawer |
| F13.4 | Save, apply and reset layout presets, `Ctrl+L` plus a digit per preset | done | all | Shell, command palette, theme drawer |
| F13.5 | Deep links to an object or a query | done | all | Shell, command palette, theme drawer |
| F13.6 | Toasts for long-running jobs | done | all | Shell, command palette, theme drawer |
| F13.7 | The explorer is a dock panel: drag, split or close it, `Ctrl+B` brings it back | done | all | Shell |
| F13.8 | The running build shown bottom right, with its commit and build time | done | all | Shell |
| F14.1 | MongoDB: collection browser, JSON editor, aggregation pipeline, index management | partial: the command console runs find, aggregate and index commands; there is no separate pipeline builder | mongodb | Query tab on a MongoDB connection |
| F14.2 | Redis: key tree, type-specific editors, TTL, command console | done | redis | Redis panel, query tab |
| F14.3 | Result renderer as a JSON tree, switchable to a table for flat documents | done | mongodb | Result area, document view |
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
