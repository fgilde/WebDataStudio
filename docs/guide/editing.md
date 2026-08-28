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

They are not editable, and the tab says so with the reason: without a key there is no safe way to
address a single row. Add a key, or edit through a statement you write yourself.

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
