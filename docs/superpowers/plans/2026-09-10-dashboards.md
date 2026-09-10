# Dashboards worth putting on a wall — Implementation Plan

**Goal:** Replace the four-column tile board with a dashboard subsystem somebody would choose over
keeping Grafana open: a 24-column canvas, fifteen widget types, one time range, variables, widgets
that span connections, and Grafana JSON in and out.

**Spec:** `../specs/2026-09-10-dashboards-that-compete-design.md`

**Architecture:** Three server units and one browser module.

- `Dashboards.cs` — the model, its JSON, and the migration from the old shape. Nothing else knows
  how a dashboard is stored.
- `DashboardSql.cs` — the only place a time macro or a variable becomes SQL. It answers
  `(Sql, Parameters)`, so the existing execution path binds what it can and the inlining that is
  left is quoted per dialect in one reviewable function.
- `GrafanaDashboards.cs` — read and write their schema, with a report of what could not come along.
- `web/src/dashboard/` — model, palette, canvas, widget frame, per-type rendering, settings drawer.

Execution reuses what exists: `POST /api/query/execute` for one connection and
`POST /api/federate/run` for several, both gaining an optional dashboard context. Masking, the row
cap, read-only connections and the audit line are therefore not touched, let alone re-implemented.

**Tech stack:** .NET 10 minimal APIs, xunit v3 (`dotnet run -- -filter "/*/*/Class/*"`), React 19 +
Mantine 9 + vitest, `gridstack` for the canvas, `echarts` for the charts.

## Global constraints

- **Every dashboard that exists keeps working.** A stored row in the old shape, a `WDS_DASHBOARD_FILE`
  in the old shape and `WithDashboards(StudioTile…)` from an app host all still load: `View` maps to
  `Type`, `Width` to `Position`, tiles lay out in the order they were written.
- **One place decides what a JSON file is.** `DashboardFormats.Read` sniffs ours, the old shape and
  Grafana, and both the import endpoint and the seeding path go through it — an app host that mounts
  thirteen Grafana files says nothing about their format.
- **Substitution is the injection boundary.** A variable binds as a parameter wherever the driver
  allows one; an inlined value is quoted per dialect, and a value that cannot be quoted is refused
  with the variable's name in the message.
- **No dual axis, ever.** Not in the model, not in the settings drawer.
- **Categorical colour is a fixed order, never cycled**; a ninth series folds into "Other". The order
  is the validated eight-hue instance in two stepped sets, bound as CSS custom properties.
- **Every chart widget can show its rows** — one item in its own menu.
- A widget that cannot render says why in its own frame. One broken widget never blanks a dashboard.
- Tests: server tests in `tests/WebDataStudio.Server.Tests`, web tests beside their component. Every
  task ends green before its commit.

---

### Task 1: The model, its JSON, and the old shape

- Create `src/WebDataStudio.Server/Services/Dashboards.cs`: `Dashboard`, `DashboardLayout`,
  `DashboardTimeRange`, `DashboardVariable`, `Widget`, `WidgetPosition`, `WidgetSource`,
  `WidgetMapping`, `WidgetOptions`, `WidgetThreshold`, `enum WidgetType`, `enum SourceKind`,
  `enum VariableKind`.
- `Dashboards.Normalise(Dashboard)` clamps every number the browser can send: positions into the
  24-column grid, `H` at least 2, refresh 0 or 10…3600, at most 60 widgets, at most 20 variables.
- `Dashboards.FromTiles(name, tiles, refresh)` — the migration, four tiles per row in the order
  written.
- Test `DashboardModelTests`: an old row becomes widgets of the right type and position; a widget
  outside the grid comes back inside it; `Options.Grafana` survives a round trip.

### Task 2: The store keeps a document

- `WorkspaceStore`: `dashboards` gains `document TEXT NOT NULL DEFAULT ''`; `ListDashboards` prefers
  it and falls back to migrating `tiles`; `SaveDashboard` writes both (the old column keeps a
  best-effort tile list, so an older image downgraded onto the same file still shows something).
- Test `DashboardStoreTests`: save/read a full dashboard; a row written by the old code reads as
  widgets; a document too large to be sensible is refused rather than stored.

### Task 3: Time macros

- `src/WebDataStudio.Server/Services/DashboardSql.cs`: `DashboardSql.Expand(sql, dialect, context)`
  where `context` carries the range, the variables and the widget's pixel width.
- `$__timeFilter(col)`, `$__from`, `$__to`, `$__fromIso`, `$__toIso`, `$__interval`.
- Relative ranges (`now-24h`, `now-7d`, `now/d`) and absolute ISO both resolve to one
  `(DateTimeOffset From, DateTimeOffset To)` in `DashboardTime.Resolve`.
- Test `DashboardSqlTests`: every macro; the filter uses the dialect's parameter prefix and timestamp
  cast; an unknown macro is left alone rather than mangled; `$__timeFilter` on a quoted identifier.

### Task 4: Variables, and the boundary

- Same file: a variable reaches SQL as `$name`, `${name}` or `${name:csv}`. Single values bind as
  parameters; a `csv` list is inlined with `dialect.QuoteLiteral` per value.
- A value that is not a plain scalar — a newline, a semicolon in a `csv` entry — is refused with the
  variable's name.
- Test: a single value binds and does not appear in the SQL text; a list is quoted and escaped
  (`O'Brien`); `${region}` and `$region` behave the same; a refused value names the variable; a
  variable nobody defined is left as it was.

### Task 5: The execution path learns the context

- `QueryEndpoints.ExecuteRequest` and `FederationEndpoints.FederateRequest` gain an optional
  `DashboardContext`. When present, each statement is expanded before it runs — per source with that
  source's dialect for a federated widget, and the joining SQL with DuckDB's.
- Test `DashboardRunTests`: a widget statement with `$__timeFilter` runs against SQLite and returns
  the rows inside the range; a federated widget with a variable in one source runs; a request without
  a context behaves exactly as before.

### Task 6: Grafana in

- `src/WebDataStudio.Server/Services/GrafanaDashboards.cs`: `Read(JsonElement) → (Dashboard, string[]
  Notes)`. Panel types per the spec's table, `gridPos` copied, `targets[].rawSql` into `Source`,
  `fieldConfig.defaults` into `Options`, `templating.list` into `Variables`, `time` into `TimeRange`.
- The datasource uid is matched to a connection by name, then by uid, and named in a note when it
  cannot be.
- What it cannot take is a note, not a swallow: a Prometheus `expr`, an unknown panel type,
  transformations, alerts, library panels. The raw panel stays in `Options.Grafana`.
- Test `GrafanaImportTests` against a real exported dashboard fixture: nine panels of six types, a
  row, a template variable, a time range, one Prometheus panel that becomes a note.

### Task 7: Grafana out

- `Write(Dashboard) → JsonObject` with `schemaVersion` pinned, panels in reading order, our types
  mapped back, `Options.Grafana` merged under what we know.
- Test: a dashboard imported and exported keeps its panel count, titles, positions and SQL; a
  dashboard built here exports with the fields Grafana needs to open it.

### Task 8: One place decides what a file is

- `DashboardFormats.Read(JsonElement) → (Dashboard?, string[] Notes)`: ours (`widgets`), the old
  shape (`tiles`), Grafana (`panels` or `schemaVersion`), and a sentence for anything else.
- Test `DashboardFormatTests`: all three shapes; an array of dashboards; a JSON that is none of them.

### Task 9: The endpoints

- Rewrite `DashboardEndpoints`: list (shipped first, then stored), save, delete, `POST /import`
  (auto-detected, answers the dashboard and the notes), `GET /{id}/grafana`.
- Seeding reads `WDS_DASHBOARD_FILE` through `DashboardFormats`, so a folder of Grafana JSON is a
  folder of dashboards.
- Test `DashboardEndpointTests`: a shipped dashboard cannot be saved over; import answers notes;
  export round trips; a stored dashboard survives a restart.

### Task 10: The browser model and the palette

- `web/src/dashboard/model.ts` — the same types, `emptyWidget(type)`, and the migration for a DTO in
  the old shape.
- `web/src/dashboard/palette.ts` — the eight-hue order in a light and a dark set, the sequential
  ramp, the diverging pair, the reserved status colours, and `seriesColour(index)` that folds past
  eight rather than cycling.
- Test `palette.test.ts`: the order is fixed; a ninth series is "Other"; the light set's three
  low-contrast slots are the ones the table-view rule names.

### Task 11: Widget data

- `web/src/dashboard/useWidgetData.ts` — runs a widget's source (single, path or federated) with the
  dashboard's range and variables, cancels on unmount, refreshes on the dashboard's interval, and
  reports an error as a value rather than throwing.
- Test: a single-source widget calls the query path with the context; a federated one calls the
  federation path; an error becomes `{ error }`.

### Task 12: Chart options

- `web/src/dashboard/charts/option.ts` — one function per family (`cartesian`, `pie`, `treemap`,
  `heatmap`, `sankey`, `gauge`) building an ECharts option from `(columns, rows, mapping, options,
  theme)`. No dual axis anywhere; a legend for two series or more; direct labels up to four.
- `web/src/dashboard/charts/EChart.tsx` — the mount, resize and dispose, with `echarts/core` and only
  the charts we use registered.
- Test `option.test.ts`: series count and colours; a legend appears at two series; a gauge without
  `Min`/`Max` returns null so the frame can say why; the axis is never doubled.

### Task 13: The widget frame and the simple types

- `Widget.tsx` — title, description tooltip, menu (show rows, duplicate, settings, delete), loading,
  error, and the table view toggle.
- `stat`, `table`, `list`, `text` (a small safe Markdown subset, no HTML), `row` (collapsible band),
  `geoMap` (the studio's own `GeoView`).
- Test: a stat shows the first cell and its threshold colour; a text widget interpolates a variable
  and renders no HTML; a chart widget toggled to rows shows the table.

### Task 14: The canvas

- `Canvas.tsx` — GridStack, 24 columns, drag/resize/reorder in edit mode and static in view mode,
  writing positions back on change.
- Test: widgets render in position order; view mode adds no drag handles.

### Task 15: The settings drawer

- `WidgetSettings.tsx` — a drawer from the right: type, title, source (connection + SQL, or several
  sources for a federated widget), the column roles read from the last result's own columns, and the
  options that type has. Thresholds as a small editable list.
- Test: changing the type keeps the source; the role selects offer the result's columns; a gauge
  demands min and max.

### Task 16: The dashboard panel

- `DashboardPanel.tsx` — the list, view and edit modes, the time range picker, the variable controls,
  the palette drawer, save/duplicate/delete, and the JSON view where a Grafana dashboard is pasted in.
- Test: the picker changes the range and re-runs; a variable control re-runs; pasting Grafana JSON
  posts to the import endpoint and shows its notes.

### Task 17: Nextended — the new model and Grafana folders

- `StudioDashboard` gains the new shape (widgets, variables, time range) while the old
  `StudioTile`-based constructor keeps working.
- `WithGrafanaDashboards(path)` mounts a file or a folder read-only and points `WDS_DASHBOARD_FILE`
  at it. Nothing says which format it is — the studio decides.
- Test `WebDataStudioDashboardTests`: the old call still writes what it wrote; a new one writes the
  new document; a Grafana folder is mounted read-only and pointed at.

### Task 18: The demo

- The demo app host gets a Grafana dashboard folder — the one Grafana itself already reads in that
  stack — so the same file opens in both, and a studio dashboard with a stat, a bar, a gauge and a
  federated widget over PostgreSQL and SQL Server.

### Task 19: Docs

- `docs/guide/dashboards.md` and `docs/guide/de/dashboards.md`, in both sidebars: the canvas, the
  types, the time range, variables, federation, Grafana in and out, and what a deployment ships.
- `docs/features.md` and the design spec's inventory: F31.1–F31.8.
- Nextended `README.md` and `docs/projects/aspire-webdatastudio.md`: the two methods.

### Task 20: The check that runs

- `web/scripts/smoke-dashboard.mjs` — rewritten: a dashboard built from the palette, a widget
  resized, a time range changed, a Grafana JSON imported, and a screenshot of the result.
