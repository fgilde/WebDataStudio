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
