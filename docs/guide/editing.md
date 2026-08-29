# Editing data

Open a table with a double-click in the explorer and the data tab is editable.

## How it works

- Double-click a cell to edit it, `Enter` to accept, `Escape` to cancel. Booleans get a switch, and
  a foreign-key column gets a dropdown of the referenced values.
- Insert, duplicate and delete rows from the toolbar.
- Nothing is sent while you type. **Save** builds the statements and shows them.

## The preview is not optional

Before a single row changes, the exact `INSERT`, `UPDATE` and `DELETE` statements are shown, in the
dialect of the target. Apply runs them in one transaction; cancel throws them away.

The preview is bound to a hash of the change set. If the change set moved on in the meantime, the
apply is refused instead of running something you did not read.

## Tables without a primary key

A key is the first choice. Without one, a unique index over columns that cannot be null addresses a
row just as well, and the studio uses it.

With neither, there is still the engine's own answer to "which row is this": PostgreSQL's `ctid`,
Oracle's `ROWID`, SQLite's `rowid`. The studio selects it alongside the row, hides it from the grid
and writes `WHERE ctid = …` — so the heap table nobody ever gave a key is editable after all.

It says what that costs, in a note above the grid: **a physical address moves when the row is
updated**, and PostgreSQL moves it again on `VACUUM`. So:

- Reload before editing again after somebody else has written to the table.
- An update made this way cannot be undone. The address moved with the write, and undoing by the old
  one would find nothing — or, after a vacuum, whatever ended up there since. A *delete* still can be
  undone: putting the row back needs no address.

MySQL and SQL Server have no usable answer — InnoDB keeps its row id to itself, and `%%physloc%%` is
undocumented and moves under you. There the tab still says the table is not editable, and means it.

## What this row looked like before

Where the database kept the answer itself, a clock next to each row opens its versions: when each
one was the truth, and what changed between them, with the columns that moved highlighted. The most
recent is marked `now`.

It reads what the *database* wrote, not what the studio saw:

| | |
|---|---|
| SQL Server | a system-versioned table (`SYSTEM_VERSIONING = ON`), read with `FOR SYSTEM_TIME ALL` |
| MariaDB | a table `WITH SYSTEM VERSIONING`, the same way |
| Oracle | flashback, so how far back it reaches is the server's undo retention — and the panel says so |
| everything else | nothing: PostgreSQL, MySQL, SQLite and the rest keep no history of a row |

The clock only appears where it can work — the table has key columns and the engine kept something —
so there is no button that answers "not supported" after a click.

Two things this deliberately is not. It is not the [audit trail](administration.md#audit): that one
knows what went through *this studio*, and the row somebody changed from an application is exactly
the one being asked about. And it is not an undo: the versions are read, never written back. Copying
a value out of an old version and pasting it into the grid is an edit like any other, with the same
preview.

## A column that holds a file

A binary column — `bytea`, `blob`, `varbinary`, `image` — shows what it weighs and two buttons: save
the file, or replace it with another one. Typing hex into a cell is not something anybody wants to
do, so those cells are picked rather than edited.

**Save** writes the file with the extension its first bytes say it has: a PDF comes out as `.pdf`,
a PNG as `.png`, and something the studio cannot name as `.bin`. It used to be `.txt` whatever was
in it, which is a file nobody can open.

**Replace** takes a file of up to 8 MB — hex doubles the size on the way to the server, and a cell
editor is not the place to move a video. The change goes through the same preview every other edit
does; in it the value reads as `0x89504e47… (12463 bytes)` rather than as a screen of hex, and the
statement writes the engine's own binary literal (`0x…`, `'\x…'::bytea`, `X'…'`) so the bytes arrive
as bytes.

## Rows from the clipboard

The clipboard button next to *insert row* turns whatever you copied — a block of cells out of Excel,
a few lines of CSV, a selection from another grid — into pending inserts.

- **Tab or comma**, whichever the first line uses. That is what a spreadsheet copies and what a CSV
  file holds, so neither needs saying.
- **A header, but only a real one.** The first line counts as a header when *every* cell of it names
  a column of this table; then each value goes where its name says rather than by position. `1,ada`
  is data, and `id,nonsense` is data too.
- **Quoted cells survive**: a comma inside quotes stays in the cell, `""` is one quote, and a line
  break inside a quoted cell does not tear the row in half.
- **An empty cell is null**, not the empty string. A spreadsheet has no way to say null, and a blank
  in a date column never meant `''`.

Nothing is written by pasting. The rows land as pending inserts, the same as typing them, and the
change-script preview shows the `INSERT` statements before anything runs.

## Bulk update

Select cells and **Bulk update** applies a value or a small expression to the selection — the
column-wide change that would otherwise be a hand-written `UPDATE`.

## Generated test rows

The wand in the data tab's toolbar fills a table with plausible rows: a name column gets names, an
email column gets addresses, a `varchar(6)` gets something that fits in six characters.

- **A strategy per column**, guessed from its name first and its type second, and correctable in the
  dialog: `name`, `email`, `city`, `sentence`, `int`, `decimal`, `date`, `uuid`, `boolean`, `fk`,
  or `skip`.
- **Foreign keys point at rows that exist** — the generator reads up to 200 keys from the parent
  table and picks from them. A foreign key with no parent rows is left null where the column allows
  it, and named in the preview where it does not, because inventing a key would break the
  constraint the column is there for.
- **A column the database fills in itself is skipped**: an identity, a serial, an `AUTO_INCREMENT`
  or a SQLite rowid alias.
- **The same seed gives the same rows**, today and tomorrow — which is what makes a generated
  dataset something two people can talk about.
- **A type the generator does not know is left to the database** rather than filled with a
  sentence. An enum, a geometry, an interval: a made-up string is refused by the engine, and rightly,
  so the column is skipped where it allows a null or has a default. Pick a strategy for it in the
  dialog to override that.
- The rows are ordinary inserts: the script is shown first and applied through the same handshake
  as a hand edit.

### Values and their types

Every value in an edit or a generated row travels to the engine as a parameter, and a parameter
travels as a string. A string is not a date, so the statement says what it is: `CAST($1 AS date)`,
using the column's own declared type. PostgreSQL is the strict one here — it refuses
`date = text` rather than guessing, which is the right call and the reason a generated date used to
come back as *"column signed_up is of type date but expression is of type text"*.

The cast is visible in the preview, because what is approved has to be what runs. Binary columns are
left alone: casting a string into one would write nonsense where an error is more honest.
