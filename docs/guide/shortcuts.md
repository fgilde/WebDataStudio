# Keyboard shortcuts

`?` opens the same list inside the app, and `Ctrl+K` opens the command palette, which reaches
every action — including the ones without a shortcut.

![Command palette](../assets/screenshots/palette-dark.png)

## Query

| Shortcut | Action |
|---|---|
| `F5`, `Ctrl+Enter` | run the selection, or the statement under the cursor |
| `Ctrl+Shift+Enter` | run the whole script |
| `Ctrl+Shift+C` | cancel the running query |
| `Ctrl+Shift+F` | format the SQL |
| `Ctrl+N` | new query tab |
| `Ctrl+S` | save the query |
| `Ctrl+H` | open the history |
| `Ctrl+Space` | completion and snippets |

## Navigation

| Shortcut | Action |
|---|---|
| `Ctrl+K` | command palette |
| `Ctrl+Shift+O` | go to an object by name |
| `F6` | refresh the explorer |
| `F12` | open the object under the cursor |
| `?` | this list |

## Tools and view

| Shortcut | Action |
|---|---|
| `Ctrl+D` | ER diagram |
| `Ctrl+E` | export the result |
| `Ctrl+T` | next theme |
| `Ctrl+B` | show the explorer, when it has been closed |
| `Ctrl+L` | layout presets |
| `Ctrl+L` then `1`…`9` | apply the saved layout in that slot |
| `Ctrl+L` then `0` | reset the layout to the default |
| `Ctrl+,` | preferences |

`Ctrl+L` opens the preset list and waits three seconds for a digit; the numbers next to the presets
appear for exactly those seconds, because that is how long they mean anything. The same dialog is
behind the layout button in the header and in the explorer's toolbar. Slot `0` is always the default arrangement, which is the way
back from a layout with every panel closed.

## Preferences and rebinding

`Ctrl+,` opens the preferences. They live in the workspace, so they survive a restart and follow you
to another browser rather than living in one machine's local storage.

| Preference | What it changes |
|---|---|
| Rows per page in the data tab | how many rows one page of a table holds |
| Keep the result with each history entry | see [result snapshots in the history](results.md#history) |
| Rows a snapshot keeps | how much of a result is kept when it is |

The **Keyboard** tab lists every command the palette knows. Click a binding, press the combination
you want, and it is stored; Escape keeps the current one, and the arrow next to a changed binding
puts the built-in one back. A rebound command works from anywhere in the studio, and the built-in
bindings for everything you did not touch keep working as before.

Modifiers are written in one fixed order (`Ctrl+Alt+Shift+K`), and on a Mac the command key counts
as `Ctrl` — the bindings are written that way throughout.

The editor is Monaco, so its own bindings work too: multi-cursor with `Alt+Click`, find and replace
with `Ctrl+F` and `Ctrl+H`, regular expressions included.
