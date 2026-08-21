# Query editor

![Query editor](../assets/screenshots/query-dark.png)

## Running statements

- `F5` or `Ctrl+Enter` runs the selection. With nothing selected, the statement under the cursor
  runs — and that statement is highlighted while you type, so you always see what will run.
- `Ctrl+Shift+Enter` runs the whole script; each statement gets its own result tab.
- The **Cancel** button stops a running statement at the server, not only in the browser.

## Single transaction

The **single transaction** switch in the toolbar wraps a whole script in one transaction: it
commits when every statement succeeded and rolls back on the first failure. Off, each statement
commits on its own, the way the engine does by default.

## Parameters

Write bind variables the way your engine spells them — `:name` for PostgreSQL and Oracle, `@name`
for SQL Server and MySQL, `$name` for SQLite — and a dialog asks for the values before the
statement runs. Values are sent as parameters, never pasted into the SQL, and the last values are
remembered per tab.

A `::text` cast, an `@@version` and a colon inside a string are not parameters and are left alone.

## Completion, hover, go to definition

Completion knows the schema of the connection the tab is bound to: tables, columns behind an
alias, keywords and snippets. Hovering a table name lists its columns; `F12` on one opens it in the
explorer.

## Snippets

Type a prefix and press `Ctrl+Space`: `sel`, `ins`, `upd`, `del`, `join`, `cte`, `idx`, `cnt` are
built in. **Manage snippets** in the command palette opens an editor for your own, which are stored
server-side and travel with your workspace. A snippet of yours with a built-in's prefix replaces it.

## Saved queries and history

Every run is written to a searchable history that survives a restart. The **Saved** panel keeps
named queries in folders; saving offers the current tab's SQL and remembers its connection.

## Formatting

`Ctrl+Shift+F` formats the buffer in the dialect of the connection.

## Notebooks

*Notebook* in the explorer's toolbar opens a document of cells: SQL cells that run against a
connection of their own with `Ctrl+Enter` and keep their result underneath, and note cells for the
prose that explains why any of it was worth running.

- Saved in the workspace, so it survives a reload and a restart.
- **Markdown in and out.** A notebook copies as Markdown — SQL cells as fenced ```sql blocks
  carrying their connection — and can be replaced from the clipboard, so an investigation can be
  pasted into a pull request or an issue and opened again from there.
- A fence that is not `sql` stays prose, so pasted JSON and shell blocks survive the round trip.
