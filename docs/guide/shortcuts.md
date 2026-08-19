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

`Ctrl+L` opens the preset list and waits three seconds for a digit; the numbers next to the presets
appear for exactly those seconds, because that is how long they mean anything. The same dialog is
behind the layout button in the header and in the explorer's toolbar. Slot `0` is always the default arrangement, which is the way
back from a layout with every panel closed.

The editor is Monaco, so its own bindings work too: multi-cursor with `Alt+Click`, find and replace
with `Ctrl+F` and `Ctrl+H`, regular expressions included.
