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
