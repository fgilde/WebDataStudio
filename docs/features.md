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
| F14.2 | Redis: key tree, type-specific editors, TTL, command console | partial: the command console covers the supported commands including TTL; there is no dedicated key tree | redis | Query tab on a Redis connection |
| F14.3 | Result renderer as a JSON tree, switchable to a table for flat documents | done | mongodb | Result area, document view |
| F15.1 | GitHub Pages site served from `/docs`, docsify like AspireUI, same visual language | done | all | docs/ site |
| F15.2 | Every page in English and German, switchable in the sidebar; docsify i18n through a `/docs/de/` tree | done | all | docs/ site |
| F15.3 | Content: getting started, environment variables, connections, query editor, results and export, schema editing, analysis, administration, engine capability matrix, shortcuts | done | all | docs/ site |
| F15.4 | Screenshots per major feature, captured in a dark and a light theme | done | all | docs/ site |
| F15.5 | README links to the site in both languages; the site links back to the repository and the GHCR image | done | all | docs/ site |
| F15.6 | A link check in CI so a renamed page cannot silently break the sidebar | done | all | docs/ site |
| F16.1 | Entra authentication for Azure SQL: `Authentication=Active Directory Default` with the container's managed identity | done | sqlserver | Connections, deployed studio |
| F16.2 | A data directory that is slow or unusable degrades to environment-only connections, reported in /api/health and in the window | done | all | Shell, /api/health |
