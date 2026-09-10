# Dashboards worth putting on a wall

**Status:** approved in conversation, 2026-09-10 — own model with Grafana import and export, Apache
ECharts, time range and variables from the start, built in one go.

## What is here now, and why it is not enough

`Dashboard { Name, Tiles[], RefreshSeconds, FromFile }` with
`DashboardTile { Title, ConnectionId, Sql, View, Width }`. Three views — a number, a table, a bar
per row — in a fixed four-column grid, refreshing on a timer, seedable from `WDS_DASHBOARD_FILE`
and from an app host through `WithDashboards`. Roughly four hundred lines of browser code, and
honest about what it is: statements side by side.

What it cannot do is everything that makes somebody keep Grafana next to the studio. No layout to
speak of. No gauge, no heatmap, no map. One connection per box and no way to put two databases in
one picture. No time range, so "last 24 hours" is typed into every statement by hand. No variables,
so one dashboard per customer. And nothing to do with the dashboards a team already has.

This replaces the model and the editor, and keeps every dashboard that exists working.

## The shape of it

Three things a widget carries, kept apart on purpose — the reference implementations put all of it
in one options bag, and that is what makes a bag of fifteen widget types unmaintainable:

- **Source** — where the rows come from.
- **Mapping** — which column plays which role.
- **Options** — how it looks.

Separating them is also what makes the Grafana mapping mechanical rather than a special case per
panel type: their `targets` are our Source, their `fieldConfig` is our Options, and the column roles
they infer from the query shape we say out loud.

```
Dashboard
  Id, Name, Tags[], RefreshSeconds, UpdatedAt, FromFile
  Layout      { Columns = 24, RowHeight = 40 }
  TimeRange   { From = "now-24h", To = "now" }        // relative or ISO
  Variables[] { Name, Label, Kind, ConnectionId?, Sql?, Values[], Multi, IncludeAll, Default }
  Widgets[]
    Id, Type, Title, Description?
    Position  { X, Y, W, H }                          // 24 columns, same as Grafana's gridPos
    Source    { Kind, ConnectionId?, Sql?, Path?, Sources[]{ConnectionId, Sql, Alias}, MaxRowsPerSource? }
    Mapping   { Time?, Category?, Series?, Values[], Latitude?, Longitude?, From?, To?, Weight? }
    Options   { Unit?, Decimals?, Min?, Max?, Thresholds[], Legend?, Stacked?, Orientation?,
                ColorMode?, Ramp?, Markdown?, Columns[]?, Limit?, Grafana? }
```

`Source.Kind` is one of three, and none of them is new machinery:

| Kind | What runs | Which engines |
| --- | --- | --- |
| `Sql` | `POST /api/query/execute`, the path a query tab already uses | every SQL engine |
| `Path` | the same endpoint with a resource path | MongoDB, Redis, OData |
| `Federated` | `POST /api/federate/run` | two or more connections in one widget |

That last row is the answer to "combine tables from different sources": `Federation` already stages
each source query into an in-memory DuckDB under an alias and runs the joining SQL there, with a row
cap and an honest report of how much it copied. A widget with several sources is that, with a
picture on the end.

## The widget types

Form follows the job the data has, not the widget somebody feels like. This is the mapping the
editor's palette is built from:

| Job | Type | Notes |
| --- | --- | --- |
| One headline number | `stat` | Hero number, optional sparkline behind it, thresholds colour the number |
| Bounded magnitude | `gauge` | Needs `Min`/`Max`; refuses to render without them rather than inventing a scale |
| Magnitude by category | `bar`, `stackedBar` | Horizontal when labels are long — that is a rule, not a preference |
| Part of a whole, few parts | `pie` | Five slices or fewer; past that the editor suggests `bar` |
| Part of a whole, many parts | `treemap` | |
| Change over time | `line`, `area`, `stackedArea` | |
| Change over time, compact | `sparkline` | For a strip of small multiples, and inside `stat` |
| Rows as rows | `table`, `list` | `table` is the studio's own grid; `list` is label-and-value, for a top ten |
| Density across two dimensions | `heatmap` | Sequential ramp, never categorical |
| Place | `geoMap` | Points from lat/lon, or a choropleth |
| Flow | `sankey` | `From`, `To`, `Weight` |
| Prose | `text` | Markdown with variables interpolated |
| Structure | `row` | A collapsible band of widgets. Exists because Grafana dashboards are full of them |

Rules that hold for every one of them, taken from the studio's visualisation guidance and not
negotiable per widget:

- **No dual axis, ever.** Two measures at different scales are two widgets, small multiples, or
  indexed to a common base. The settings drawer has no second-axis switch to find.
- **Categorical colour is assigned in a fixed order and never cycled.** A ninth series folds into
  "Other" or the widget facets; it never gets a generated hue.
- **Two series or more means a legend is present**, and four or fewer are also labelled directly.
  Identity is never carried by colour alone.
- **Text wears text colours.** Values, labels and legends stay in the theme's ink; the coloured mark
  beside them carries the identity.
- **Status colours are reserved** for thresholds — good, warning, serious, critical — and never
  reused as "series four". A threshold state ships with its number, not just a colour.
- **Every chart widget can show its rows.** One toggle in its menu turns it into the table it came
  from. That is the accessibility fallback and the debugging tool in the same switch.
- **Hover is not optional**: crosshair and tooltip on time-shaped forms, per-mark tooltip on bars,
  cells and points.

## Colour across twenty-three themes

The studio ships twenty-three themes, each declaring `scheme: "light" | "dark"` and its own surface
ramp. Charts do not get twenty-three palettes. They get one validated categorical order in two
stepped sets — one for light surfaces, one for dark — bound as CSS custom properties on the chart
root, so a widget is written against roles (`--series-3`) rather than hex.

The order is the eight-hue reference instance from the visualisation guidance: blue, orange, aqua,
yellow, magenta, green, violet, red. It clears the colour-vision-deficiency and normal-vision gates
on adjacent pairs in both modes. Two consequences the editor has to respect:

- Forms where **any** two series can end up side by side — a choropleth, a scatter — cap at three
  series before folding to "Other". Past three the palette cannot clear the all-pairs floor, and
  re-stepping a documented palette is not on the table.
- Three of the light slots sit below 3:1 on a light surface, so those series ship visible direct
  labels or the table view. The relief rule, not a warning to dismiss.

Sequential encoding (heatmap, choropleth) is the blue ramp, light to dark. Diverging is blue↔red
with a grey midpoint. Validation is not eyeballed: the guidance ships a validator, and it runs
against the **lightest and the darkest surface among the themes** — the two extremes that can fail
contrast — rather than against all twenty-three.

## Time range and variables

A dashboard has one time range, relative (`now-24h`) or absolute, and a picker in its header. Widget
SQL reaches it through macros, expanded server-side per dialect:

| Macro | Becomes |
| --- | --- |
| `$__timeFilter(placed_at)` | the engine's own `BETWEEN` over the range |
| `$__from`, `$__to` | epoch milliseconds |
| `$__fromIso`, `$__toIso` | ISO timestamps |
| `$__interval` | a bucket width from the range and the widget's pixel width |

Variables are `Query` (values from a statement on a connection), `Custom` (a written list),
`Constant` or `Interval`, single or multi, optionally with an "All" entry. They appear as controls in
the dashboard header and reach SQL as `$region`, `${region}` or `${region:csv}` for a list.

**Substitution is the injection boundary, and the spec says so out loud.** A variable value is bound
as a parameter wherever the engine's driver allows one; where a value must be inlined — an
identifier, an `IN` list — it is quoted per dialect and refused if it cannot be. A dashboard is a
document people paste to each other; it does not get to be a way to run arbitrary SQL under somebody
else's account. Everything else already applies unchanged: a widget's statement runs through the
same path a query tab uses, so masking, the audit line, read-only connections and the row cap are
not re-implemented here.

## Grafana, in and out

Import reads a dashboard JSON and answers with what it made and what it dropped.

| Grafana | Here |
| --- | --- |
| `panels[].type` | `stat`, `timeseries`→`line`, `barchart`→`bar`, `piechart`→`pie`, `gauge`, `table`, `text`, `geomap`, `heatmap`, `row` |
| `gridPos {x,y,w,h}` | `Position` — both are 24 columns, so it is a copy |
| `targets[].rawSql` | `Source.Sql`; the datasource uid is matched to a connection, and asked for when it cannot be |
| `fieldConfig.defaults` | `Unit`, `Decimals`, `Min`, `Max`, `Thresholds` |
| `options.legend`, `stacking` | `Legend`, `Stacked` |
| `templating.list` | `Variables` |
| `time.from`, `time.to` | `TimeRange` |

What it cannot take, it names rather than swallows: a Prometheus or Loki `expr`, a panel type with no
match here, transformations, alert rules, library panels. The report is a list of sentences, one per
panel, and the raw panel JSON is kept in `Options.Grafana` so an export puts back what came in.

Export writes the same schema with `schemaVersion` pinned, so a dashboard built here opens in
Grafana. Neither direction claims to be lossless; both say what they did.

## The editor

GridStack as the canvas — twenty-four columns, drag, resize, reorder — because that is the library
the reference implementations used and it does the one job well. A palette drawer adds a widget; the
widget's own settings live in a drawer from the right, per type, with the source picker, the column
roles read from the query's own result, and the options that type has. Duplicate, delete, and a
JSON view for the whole dashboard, which is also where a Grafana JSON is pasted in. Edit and view are
two modes, and view is the default.

## Seeding, and the app host

`WDS_DASHBOARD_FILE` keeps working. A file in the old shape still loads: `View` maps to `Type`,
`Width` to `Position`, and the tiles lay themselves out in the order they were written — the same
migration the store runs for dashboards already saved.

Nextended gains the new model in `WithDashboards`, and one method that matters for adoption:
`WithGrafanaDashboards("./dashboards")`, a folder of Grafana JSON mounted and imported at start —
because a team that has thirteen of those files has them in exactly that shape.

## Not in this

Alerting and notification rules. Annotations. Scheduled PDF or image export. Live-streaming panels.
Prometheus, Loki or any datasource that is not a connection this studio already has. A plugin API for
third-party widget types. Each of those is a feature of its own, and none of them is what makes
somebody keep the other tool open.

## Features

| Id | What |
| --- | --- |
| F31.1 | A dashboard as a canvas: twenty-four columns, drag, resize, reorder, edit and view as two modes |
| F31.2 | Fifteen widget types, each with the source, the column roles and the options its form needs |
| F31.3 | A widget over several connections at once, through the studio's own federation |
| F31.4 | One time range per dashboard, and the macros a statement reaches it through |
| F31.5 | Dashboard variables — from a query, a list or a constant — bound as parameters wherever the driver allows |
| F31.6 | Grafana dashboards imported and exported, with a report of what could not come along |
| F31.7 | Dashboards a deployment ships, in the studio's own shape or as Grafana JSON |
| F31.8 | One validated colour order across every theme, with the table view where contrast cannot carry it |
