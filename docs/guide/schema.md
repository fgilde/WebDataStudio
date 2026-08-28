# Schema editing

![Table designer](../assets/screenshots/designer-dark.png)

## Table designer

**Design table…** in the explorer's context menu opens a table: columns with types, nullability,
defaults, identity and comments; indexes with uniqueness, a filter and include columns where the
engine supports them; primary keys, foreign keys, unique and check constraints.

Every change is a migration preview first. The dialog shows the statements, marks the destructive
ones, and says whether they run in one transaction. Nothing reaches the database until you apply.

SQLite has no `ALTER COLUMN`; the designer writes the create-copy-drop-rename sequence for you and
shows it in the preview like any other change.

## Indexes

**Indexes…** on a table opens the designer on its index tab: name, columns, uniqueness, a partial
predicate and include columns where the engine has them. **Add index on this column…** on a column
starts the same editor with that index already filled in.

Where the engine has full text — PostgreSQL and MySQL — a *Full text* switch turns the index into
the engine's own spelling of it: a GIN index over `to_tsvector(...)` on PostgreSQL, a
`FULLTEXT INDEX` on MySQL. Everything else keeps the switch hidden rather than writing a statement
that would fail.

Index changes go through the same preview as any other schema change.

## Scripts from the context menu

`Script: INSERT`, `UPDATE`, `DELETE` and `TRUNCATE` open a query tab with the statement already
written for the object you picked. Columns, indexes and foreign keys have their own: `DROP COLUMN`,
`DROP INDEX`, a rebuild, `DROP CONSTRAINT`. Destructive statements are never run from a menu — they
land in the editor, where you read them and press `F5` yourself.

## Rename

**Rename…** shows the statement together with what depends on the object, so a rename that would
break a view is a decision rather than a surprise. What the statement is depends on what is being
renamed: `ALTER VIEW`, `ALTER SEQUENCE`, `ALTER TRIGGER … ON`, `sp_rename` on SQL Server. A routine
is identified by its argument types, which the tree does not carry — that one says so instead of
writing a statement no engine can resolve.

## Views, procedures, functions, triggers

**Edit source…** opens the definition in an editor with the engine's own text in it. Saving shows
the statement first, exactly like the table designer:

- a **view** opens as its `SELECT` — the studio writes the `CREATE` around it, because every engine
  spells "replace this definition" differently (`CREATE OR REPLACE` on PostgreSQL and MySQL,
  `CREATE OR ALTER` on SQL Server, a drop and a create on SQLite, both shown);
- a **procedure, function or trigger** opens as the whole statement, which is what somebody who
  wrote one expects to see back. SQL Server sends it as `CREATE OR ALTER` whatever the source says,
  so saving an existing routine does not fail with "there is already an object named …"; MySQL
  cannot replace one in place, so its preview holds the drop and the create.

**New view…**, **New procedure…**, **New function…** and **New trigger…** are on the folder that
holds them, and start from a template rather than an empty box.

A trigger can also be **switched off** rather than dropped — `ALTER TABLE … DISABLE TRIGGER`, or
`DISABLE TRIGGER … ON` on SQL Server. MySQL and SQLite have no such thing and say so.

## Sequences

**Change…** on a sequence writes the `ALTER`: increment, minimum, maximum, cache, cycle — and
**restart**, which is the one people actually come for. An import that wrote its own ids leaves the
sequence counting from where it was, and the next insert collides; setting it above the largest id
in use fixes that in one statement. A restart is marked destructive, because it can hand out ids
that already exist.

**New sequence…** is on the Sequences folder. MySQL and SQLite have no sequences and say what to
use instead (`AUTO_INCREMENT`, `INTEGER PRIMARY KEY`).

## Schemas, descriptions and dropping

**New schema…** is on the database, **Drop schema…** on the schema — the drop asks whether
everything in it goes too (`CASCADE`), because that is the whole question. In MySQL a schema is a
database, so it points at **New database…** instead.

**Description…** writes the description the database itself keeps (`COMMENT ON`), which is what
another tool reading this database sees. It is available for tables, views, columns, sequences and
routines on PostgreSQL, and for tables on MySQL. SQL Server keeps descriptions as extended
properties and SQLite keeps none at all; on those two the studio's own
[notes](explorer.md) are the place for one — they need no rights and no migration.

**Drop…** replaces the old "Script: DROP" on every object kind: the statement is shown with
everything that depends on the object listed next to it, and only a click runs it. That is the same
path a table change takes, and the reason a drop that would break a view is a decision rather than a
surprise.

Object editors are hidden on an engine the studio writes no DDL for: PostgreSQL, MySQL, SQL Server
and SQLite have one, and the rest take a statement in a query tab.

## Snapshots and drift

With `WDS_SCHEMA_SNAPSHOT_DIR` set, the studio writes a snapshot of every connection's schema
shortly after it starts and reports what moved since the last one: tables added or removed, and per
table which columns, indexes and foreign keys came or went.

```bash
WDS_SCHEMA_SNAPSHOT_DIR=/data/snapshots
```

- `GET /api/schema/{connection}/drift` — what moved, or `no change`.
- `POST /api/schema/snapshot` — take one now, which is the answer to "did my migration do what I
  think it did".
- The drift is also a log line, and a message when [alerts](administration.md#alerts) are
  configured.

The first snapshot is a baseline, not a change. Each snapshot then becomes the baseline for the next
comparison, so a change is reported once. Files are written through a temporary name, and a file
that cannot be read is treated as no file rather than as a schema that vanished.

What it does **not** do is version your schema — that is a migration tool's job. This catches the
drift a migration tool cannot see: the column somebody added by hand on staging at 23:40.
