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
