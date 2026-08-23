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

## What the tree shows

Under a connection PostgreSQL keeps more than its schemas, and the tree lists that next to them:

| Folder | What is in it |
|---|---|
| Extensions | every installed extension with its version |
| Roles | login roles and groups, superusers marked as such (the `pg_` built-ins are left out) |
| Tablespaces | where the data actually lies |
| Publications / Subscriptions | logical replication, both ends of it |
| Types and domains | per schema: enums, composites and domains |

They are read-only listings — the studio names what is there rather than offering to change it. The
other engines show their schemas as before; a folder they have no catalogue for is left out rather
than shown empty.

## Privileges for a whole schema

**Privileges on everything here…** on a schema builds one script instead of one dialog per table:
the privileges you tick, for the role you name, on every table in that schema. On PostgreSQL that is
`GRANT … ON ALL TABLES IN SCHEMA`, plus `ALTER DEFAULT PRIVILEGES` when "also for tables created
later" is on — without that second half a table created tomorrow is not covered. The other engines
get one statement per table, because that is what they have.

The script opens in a query tab. Nothing changes until it runs there.

## Refreshing a materialised view

Materialised views sit in the **Views** folder next to the ordinary ones, with their own icon and
their own menu — `pg_views` leaves them out, so before this release PostgreSQL never showed one at
all.

A materialised view has **Script: REFRESH** and **Script: REFRESH CONCURRENTLY**. Concurrently keeps
the view readable while it rebuilds and needs a unique index on it; plain is faster and locks it.
Both come back as statements from the server, which knows how the engine spells it — Oracle's is
`DBMS_MVIEW.REFRESH` — and refuses to build one for an object that is not a materialised view.

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

Selecting a table or view fills the structure panel. Beyond its columns, indexes and keys, these
tabs answer the questions people otherwise go looking for in a catalogue by hand:

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

**Policies** — on a table, whether row-level security is on, whether it is forced for the owner
too, and every policy with what it applies to and the expression behind it. The field below builds a
`CREATE POLICY`, the bin a `DROP POLICY`. Security that is on with no policy means "nobody but the
owner sees anything", and the tab says so rather than letting it look like an empty list of rules.
PostgreSQL only — it is a PostgreSQL feature, and the other engines say that instead of showing
nothing.

**Partitions** — on a partitioned table, how it is cut up (`RANGE`, `LIST`, `HASH` and the key),
with every partition, its bound, its size and its row estimate. Detaching leaves the data behind as
a table of its own; attaching takes an existing table in and needs the bound spelled out. Both are
statements, and "detach concurrently" is offered because it does not block readers — and cannot run
inside a transaction.

**Inspect** — on a function or procedure: its language, what it returns, its declared parameters,
its source, and a **run that is rolled back**. Fill in the arguments, press the button, and the
studio runs it inside a transaction it always rolls back, showing what came back, how long it took
and every `RAISE NOTICE` it raised on the way.

This is not a stepping debugger: there are no breakpoints and no variable inspection. For PL/pgSQL
it covers what most of that debugging actually is. Two things to know: a side effect PostgreSQL
keeps outside the transaction — a sequence that moved, a `dblink` call — survives the rollback, and
a read-only connection refuses the run rather than pretending the rollback makes it safe.

**SQL** — the object as a `CREATE` statement, to copy or to open in a query tab. Engines that keep
the original text hand that over; for the rest the studio generates it from the shape it read.

## A column from the other side of a key

The column menu of a table browse offers the columns of the table a foreign key points at: pick one
and it appears next to the id, marked **borrowed**. The join happens on the server, so sorting and
filtering still work on the table's own columns, and the borrowed column is read-only — an edit there
would be an update to a row this grid is not addressing.

Only single-column keys are offered. A composite key cannot be followed by comparing one value, and
showing the wrong row would be worse than not offering it.

## Perspective — a row and everything related to it

The **Perspective** panel starts from one table and lets a row be opened: what it points at, and
what points back at it, each as a nested list, as deep as you care to open. It reads the same
foreign-key graph the ER diagram draws, so nothing has to be typed.

Each level is one page of rows rather than the whole table, and each opened relation is one query —
they are collapsed until asked for. Only single-column keys are followed, for the same reason as
above. For paging through a table in full, the data tab is still the right place.

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
