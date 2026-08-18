# WebDataStudio implementation plans

All plans implement `../specs/2026-08-18-webdatastudio-design.md`. Execute them in order; each phase
ends with a working, shippable image.

| Plan | Phase | Feature IDs |
|---|---|---|
| [2026-08-18-p0-skeleton.md](2026-08-18-p0-skeleton.md) | Repository, server, SPA shell, auth, connection store, Docker image, GHCR workflow | F1.1–F1.3, F1.6, F13.3 |
| [2026-08-18-p1-drivers-tier1.md](2026-08-18-p1-drivers-tier1.md) | Driver abstraction, PostgreSQL, MySQL, SQL Server, SQLite, object explorer, streaming execution | F2.1–F2.6, F4.1–F4.4, F4.6 |
| [2026-08-18-p2-editor-grid.md](2026-08-18-p2-editor-grid.md) | Monaco editor, selection execution, completion, virtualised grid, history, tab persistence | F3.1–F3.7, F3.10, F3.12, F3.13, F4.8, F5.1–F5.7 |
| [2026-08-18-p3-export-import.md](2026-08-18-p3-export-import.md) | Streaming export in every format, import, cross-engine table copy | F7.1–F7.7 |
| [2026-08-18-p4-data-editing.md](2026-08-18-p4-data-editing.md) | Grid editing, change-script preview, foreign-key navigation, transactions | F4.5, F5.8, F6.1–F6.6 |
| [2026-08-18-p5-plans-analysis.md](2026-08-18-p5-plans-analysis.md) | Execution plans, index advisor, deep analyze, server statistics | F9.1–F9.8 |
| [2026-08-18-p6-schema-editing.md](2026-08-18-p6-schema-editing.md) | DDL writers, table designer, routine editors, migration preview | F8.1–F8.7 |
| [2026-08-18-p7-tier2-tier3-engines.md](2026-08-18-p7-tier2-tier3-engines.md) | Oracle, DuckDB, ClickHouse, MongoDB, Redis | F14.1–F14.3 plus capability extensions |
| [2026-08-18-p8-diagrams-compare-admin.md](2026-08-18-p8-diagrams-compare-admin.md) | ER diagrams, schema and data compare, backup/restore, administration | F4.7, F10.1–F10.3, F11.1–F11.5, F12.1–F12.3 |
| [2026-08-18-p9-usability.md](2026-08-18-p9-usability.md) | SSH tunnels, pooling, parameters, snippets, saved queries, query designer, charts, palette, layouts | F1.4, F1.5, F1.7, F1.8, F3.8, F3.9, F3.11, F3.14, F5.9–F5.12, F13.1, F13.2, F13.4–F13.6 |

All 96 feature ids from the spec are covered exactly once across these plans; nothing appears in a
plan that is not in the spec.

## Plan depth

P0, P1 and P2 are written at step level: every test, every implementation file and every command is
spelled out, because the spec fully determines them.

P3 through P9 are written at task level: exact files, exact interface signatures, the assertions each
test must make, and the commit points — but the implementation bodies are described rather than
transcribed. They depend on decisions that only become concrete once the earlier phases exist, and a
transcribed body written today would be fiction by the time it is read.

**Before starting a phase from P3 onward, re-run the writing-plans skill on that plan** to expand it
to step level against the code as it then stands. The task boundaries, interfaces and test lists in
these files are the input to that pass, not scaffolding to be thrown away.
