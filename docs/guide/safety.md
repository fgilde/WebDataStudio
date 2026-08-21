# Safety

Three things that make a shared studio safe to open in front of other people: secrets are not on
screen by default, a change can be taken back, and not everybody who signs in can do everything.

## Masked columns

A column whose name says it holds a secret — `password`, `password_hash`, `api_key`, `token`,
`iban`, `card_number`, `cvv`, `pin`, … — arrives masked as `••••••••`, with a lock next to its
header. This happens on the **server**: the real value is not in the response, so it cannot be read
out of a network tab, a proxy log or a screenshot of somebody's developer tools.

The heuristic is deliberately narrow. It matches whole words inside a name, so `userPassword` is a
secret while `password_changed_at` is a timestamp and stays visible. A false positive costs one
click; a false negative leaks.

**Revealing** is a fresh request. The *Masked* button in the data tab's toolbar turns into
*Revealed* and re-fetches the page with `?reveal=true`. Nothing is cached on the way.

**Correcting the guess** happens in a column's menu: *Always mask this column* or *Never mask this
column*. Both lists are stored per connection in the workspace database and apply to everybody who
opens that connection — somebody who knows the schema knows it better than a word list does.

Where it applies:

| Path | Behaviour |
|---|---|
| Data tab (`GET /api/data/{conn}`) | masked; `?reveal=true` returns the values |
| Query results (`POST /api/query/execute`) | masked — a query is the other way into the same data |
| Export (`POST /api/export/{format}`) | masked; `includeSensitive: true` returns the values |
| Export on a **red** connection | `includeSensitive` is refused outright |

A file leaves the building, so an export marked as production (colour red, the studio's convention
for it) will not carry unmasked secrets at all. Remove the colour if that is really intended.

## Undo

Applying a change records its inverse, built from the rows as they actually were, read inside the
same transaction that changed them. The data tab then shows an undo arrow saying what it would take
back ("2 updates").

Undo goes through the same handshake as every other change: the inverse script is shown first and
only runs when it is approved.

- An update goes back to the old values of exactly the columns that were written.
- A delete comes back whole, every column.
- An insert's inverse is a delete by its key. A key the database generated is not in the request, so
  such a step reports itself as not undoable rather than deleting by a guess.
- The last twenty steps per connection are kept. Without a usable workspace database there is no
  undo, and the button is simply not offered.
- An undo is not itself recorded: one step back is a model people can hold, "undo the undo" is not.

## Accounts and roles

Without `WDS_USER`/`WDS_PASSWORD` or `WDS_USERS` the studio runs open and shows no login screen.

`WDS_USERS` holds one entry per account, separated by `;`:

```
name:role:secret[:connection,connection]
```

```bash
WDS_USERS='ada:admin:pbkdf2$210000$c2FsdA==$aGFzaA==;grace:viewer:pbkdf2$...:PROD,STAGING'
```

- **Roles.** `admin` sees everything and reaches the administration surface. `editor` may read and
  write but not administer. `viewer` gets every connection read-only. An unknown role is read as
  `viewer` — a typo must not grant more than intended.
- **Connections.** The optional fourth field is a whitelist of connection names or ids. Empty means
  all of them. A connection an account may not see does not exist for it: not in the list, and not
  by guessing its id either.
- **Secrets.** Either a PBKDF2 hash (`pbkdf2$iterations$salt$hash`, SHA-256, 210 000 iterations) or
  a literal password, which is what `WDS_PASSWORD` has always been. The *Studio users* tab in the
  administration panel says which of the two each account uses.
- `WDS_USER`/`WDS_PASSWORD` keep working and mean one admin.

Accounts are deployment configuration, not stored state. They come from the environment, so a
container rollout is the only way to change them and nobody can grant themselves a role through the
UI. The *Studio users* tab lists who exists; it does not edit.

Generating a hash — the *Studio users* tab has a field for it, or by hand against a running studio
signed in as an admin:

```bash
curl -sX POST http://localhost:8080/api/admin/studio-users/hash -H 'content-type: application/json' -d '{"password":"the password"}'
```

## What this is not

Masking is a guard against shoulder-surfing, screenshots and careless exports — not a substitute
for the database's own permissions. A user who may `SELECT` a column can still read it through a
connection of their own. Give the studio a database account that reaches only what it should, and
use `viewer` roles and `WDS_READONLY` where writing has no business happening.
