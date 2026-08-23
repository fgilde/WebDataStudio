# Explorer and panels

## Searching

The box above the tree searches **tables and views** — the objects people go looking for. Type two
characters or more and the tree is replaced by a flat list of matches, each row showing the
connection and schema it belongs to; empty the box and the tree comes back exactly as it was.

Matching is by subsequence, so `ordit` finds `order_items` and `abpu` finds `AbpUsers`. Results are
ranked: an exact name first, then a name that starts with what you typed, then a match at a word
boundary, then the rest.

A result row behaves like a tree row — click to select, double-click to open the data, right-click
for the same context menu.

> Until this release the box filtered the **first** level of the tree, which is schemas on
> PostgreSQL, folders on SQLite and database numbers on Redis — never tables. Typing a table name
> emptied the tree, and typing `tab` matched the folder called "Tables".

The list comes from the same cached walk the editor's completion and `Ctrl+Shift+O` use, so the
first search on a connection costs one pass over its schema and later ones are instant. The refresh
button drops that cache, which is what to press after somebody else changed the schema.

## Panels

Every panel is a dockview panel: drag it by its tab, drop it anywhere, split the group, and the
arrangement is saved (see [layout presets](shortcuts.md)).

Right-click a tab for:

| Action | What it does |
|---|---|
| Close | closes this panel |
| Close others | closes every other panel, except pinned and protected ones |
| Close to the right | closes the tabs after this one in the same group |
| Close all | closes everything closable |
| Pin — keep it open | the tab loses its × and survives "close others" and "close all" |
| Maximize / Restore | the group fills the studio, or goes back |
| Open in its own window | the group moves into a separate browser window |

The explorer and the start page are never closed by "close others" or "close all": they are the way
back to everything else.

### A panel in its own window

"Open in its own window" opens a real browser window, so a popup blocker can stop it — the panel
then stays where it was. The window carries the studio's theme, and closing it docks the panel back
into the studio. A second monitor with the query editor on one side and the result on the other is
what this is for.

## What the structure panel answers

Selecting a table or view fills the structure panel. Beyond its columns, indexes and keys, four tabs
answer the questions people otherwise go looking for in a catalogue by hand:

**Statistics** — how big the object is (total, table, indexes), how many rows the engine thinks it
has, how many of them are dead, when it was last vacuumed and analysed, and how often it is read by
a scan rather than an index. Underneath, every index with its size and its scan count: an index with
**0 scans** that is not a primary key is the clearest "delete me" a database ever gives you.
PostgreSQL, MySQL, SQL Server and Oracle answer this; SQLite keeps no such statistics and the tab
says so rather than showing a table of blanks.

**Privileges** — who has what on this object, and whether they may pass it on. The field above the
list builds a `GRANT`, and the bin next to a row builds the matching `REVOKE`. Neither runs: the
statement opens in a query tab, where it goes through the same preview as any other change. It lists
**grants**, not effective access — an owner and a superuser still reach the object without one.

**Dependencies** — what breaks if this changes, and what this needs. The question before every
`DROP`. On an engine with no dependency catalogue (SQLite) the answer is a search of the other
objects' definitions, and the tab says as much.

**SQL** — the object as a `CREATE` statement, to copy or to open in a query tab. Engines that keep
the original text hand that over; for the rest the studio generates it from the shape it read.

## Query plans

The plan panel reads the plan rather than only drawing it. Besides the heat map over node costs, the
findings list names the shapes that are usually the problem:

| Finding | What it means |
|---|---|
| Sequential scan over many rows | an index on the filtered or joined column turns it into a lookup |
| Nested loop over a scan | the inner side is scanned once per outer row |
| Nested loop over many rows | a hash or merge join is usually the shape for that many |
| Row estimate off by 10× or more | the planner is choosing on stale statistics — `ANALYZE` |
| Sort or hash spilled to disk | it did not get the memory it wanted (`work_mem`, `sort_buffer_size`) |
| Anything the engine itself warned about | passed through as the engine said it |

Each carries the statement that would fix it where there is one, and that statement goes through the
migration preview like everything else.
