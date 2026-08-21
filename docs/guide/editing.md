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
- The rows are ordinary inserts: the script is shown first and applied through the same handshake
  as a hand edit.
