# Results and export

Results stream in while the query is still running, so the first rows are on screen long before the
last one arrives. The row cap (`WDS_MAX_ROWS`, default 1000) can be raised per run.

## Views

A switch above the grid picks how to read the same result:

| View | Good for |
|---|---|
| Grid | the default; virtualised, so a hundred thousand rows scroll smoothly |
| Form | one row at a time when a table has forty columns |
| Transposed | a wide table with few rows — columns become rows |
| Chart | bar, line or pie over one label column and one or more numeric columns |
| Compare | two results side by side, matched by key columns |

![Chart view](../assets/screenshots/chart-dark.png)

## In the grid

- Sort, filter per column, and search the whole result.
- Hide, pin, reorder and resize columns; widths are remembered by column name.
- Group by a column: each group shows its count and the sums of the numeric columns.
- Select cells and the status bar shows count, sum, average, minimum and maximum.
- Double-click a cell to open the value viewer: text, JSON, XML, hex, images and BLOB download.
- `NULL` is drawn differently from an empty string, because the difference matters.

## Export

The export dialog writes CSV, TSV, Excel, JSON, NDJSON, XML, YAML, Markdown, HTML, SQL inserts,
SQL schema and Parquet. The scope is the current result, a whole table or a whole schema. Delimiter,
encoding, quoting, header row, `NULL` representation and date format are all yours to set.

Exports stream: the server never builds the whole file in memory, so a million-row CSV costs the
same memory as a thousand-row one.

## Copy

The **Copy** menu puts the result on the clipboard as CSV, JSON or a Markdown table, and a
selection as a SQL `IN` list — the fastest way to move a set of ids into the next query.

## Import

**Import into this table…** in the explorer's context menu reads CSV, Excel, JSON or SQL, shows a
preview, and lets you map file columns to table columns. Rows that fail are reported one by one
rather than aborting the whole file.

**Copy to another connection…** moves a table between two connections, including across engines.

## Watching a query

The interval box in the query toolbar re-runs the statement every 2, 5, 10 or 30 seconds and
highlights the cells that changed since the previous run, with a note saying what moved — "2
changed, 1 added, 1 gone".

- One run at a time: the next is scheduled when the previous finished, so a slow query cannot pile
  up behind its own interval.
- An error stops the watch and says why, rather than retrying into the same wall.
- Editing the SQL restarts the comparison: watching a query you have since changed would compare
  the wrong things.
- The highlight is skipped while the grid sorts or filters, because then a row's position is no
  longer the row the comparison was about.

## Browsing a table

A table opened with a double-click gets the same **Copy** and **Export** actions as a query result:
copy takes the page on screen, export streams the whole table. Its column headers carry a menu for
sorting and filtering, and both run on the server — a page holds 200 of possibly millions of rows,
so sorting in the browser would order the wrong set.

## Sharing a result

With sharing on, a result grows a **Share** button: the rows are kept as they are and you get a link
to them. It is the answer to "here is what I am seeing" that is not a screenshot.

| Variable | Meaning |
|---|---|
| `WDS_SHARE_ENABLED` | `true` allows sharing. Off by default |
| `WDS_SHARE_PUBLIC` | `true` lets anybody with the link open it, without signing in |
| `WDS_SHARE_TTL_HOURS` | how long a link lives, default `168` (a week) |
| `WDS_SHARE_MAX_ROWS` | rows a link keeps, default `1000` |

A link is a **snapshot**, and that is the whole design:

- It shows the rows as they were. Changing the table afterwards does not change the link, and the
  link cannot run anything.
- It cannot show more than the person who made it could see: masking is applied **before** the rows
  are stored, so a masked column is masked in that link for good.
- Only a reading statement can be shared.
- It expires. An expired link and one that never existed answer the same way, because a link that
  used to work should not say what it used to show.
- Ids are 128 random bits: a link anybody with it can open must not be a link anybody can guess.

`WDS_SHARE_PUBLIC=true` is the part worth thinking about — it puts those rows behind nothing but the
URL. Off, a link still needs an account on the studio.

## Scheduled queries

`WDS_SCHEDULE_FILE` points at a JSON file of queries the studio runs on its own and writes as files —
the nightly report nobody wants to remember to run.

```bash
WDS_SCHEDULE_FILE=/data/schedule/schedule.json
WDS_SCHEDULE_OUTPUT_DIR=/data/exports          # the default
```

```json
[
  {
    "name": "orders-per-day",
    "connection": "SHOP",
    "sql": "SELECT date(created_at) AS day, count(*) FROM orders GROUP BY 1 ORDER BY 1",
    "dailyAtUtc": "03:00",
    "format": "csv"
  },
  {
    "name": "queue-depth",
    "connection": "SHOP",
    "sql": "SELECT count(*) FROM jobs WHERE state = 'pending'",
    "everyMinutes": 15,
    "format": "json"
  }
]
```

- **Reading only.** A scheduled statement goes through the same guard the MCP `run_query` tool uses,
  so a schedule file cannot become a way to run a `DELETE` at 03:00 every night.
- **`everyMinutes` or `dailyAtUtc`**, not cron: the studio is not a scheduler, it runs a report.
- Masking applies — a file is a file that leaves the machine.
- The file is re-read every minute, so editing the schedule needs no restart.
- `GET /api/schedule` reports the jobs and what each last did; `POST /api/schedule/{name}/run` runs
  one now. A failed run is a message when [alerts](administration.md#alerts) are configured.
