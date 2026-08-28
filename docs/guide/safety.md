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

**Setting it from the deployment** is the other way, for a schema whose names the word list reads
wrong:

| Variable | Meaning |
|---|---|
| `WDS_MASK_EXTRA` | columns to mask as well, comma-separated |
| `WDS_MASK_NEVER` | columns to leave alone |
| `WDS_MASK_DEFAULT` | `false` turns the name heuristic off, leaving only `WDS_MASK_EXTRA` |

```bash
WDS_MASK_EXTRA=ssn,iban,customer_note
WDS_MASK_NEVER=token_type
```

These are the baseline for every connection. A column somebody set from the column menu wins over
them, because they were looking at the data at the time. The mask-policy call reports which rules
came from the environment, so the UI can say that.

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

The header shows a person icon once accounts exist: who is signed in, their role and what it may
do, and **Sign out**. On a studio without accounts there is nothing to be and no menu.

Accounts are deployment configuration, not stored state. They come from the environment, so a
container rollout is the only way to change them and nobody can grant themselves a role through the
UI. The *Studio users* tab lists who exists; it does not edit.

Generating a hash — the *Studio users* tab has a field for it, or by hand against a running studio
signed in as an admin:

```bash
curl -sX POST http://localhost:8080/api/admin/studio-users/hash -H 'content-type: application/json' -d '{"password":"the password"}'
```

## Kept before it goes

The editor warns about a `DELETE` with no `WHERE` before it runs, and there is one step of undo for
cell edits. Between those sits the case that ruins an afternoon: the statement ran, it took every
row, and undo was never about statements.

So for exactly the statements that take **everything** — a `DELETE` or an `UPDATE` with no `WHERE`, a
`TRUNCATE` — the table is read into an [archive](results.md#archives) first, and the run says so in
its messages:

```
3417 row(s) of orders were kept as the archive 'orders-before-20260828-141233'
```

An archive is a file the studio lists, reopens as a grid and scripts back out as inserts, which is
what makes this a way back rather than a comfort.

| Variable | Meaning |
|---|---|
| `WDS_SAFETY_NET` | `false` turns it off, for somebody who means it every time |
| `WDS_SAFETY_MAX_ROWS` | how many rows are kept, default `20000` |

Three decisions worth knowing. A statement **with** a `WHERE` keeps nothing: that one is somebody
being specific, and reading a table nobody asked to read is its own kind of surprise. Masked columns
stay masked in the copy, because the rule does not change when the reason is a good one. And a copy
that could not be taken is said out loud before the statement runs, but it does not refuse the
statement — `WDS_SAFETY_NET=false` is how to mean that.

## Signing in with an identity provider

`WDS_USERS` is a list of accounts in a container's environment: fine for one team, wrong for an
organisation that already decides who works there somewhere else. With an authority and a client id
configured, the login screen offers that provider instead — Entra, Keycloak, Auth0, Okta, anything
that speaks OpenID Connect — and the studio never sees a password.

```bash
WDS_OIDC_AUTHORITY=https://login.microsoftonline.com/<tenant>/v2.0
WDS_OIDC_CLIENT_ID=00000000-0000-0000-0000-000000000000
WDS_OIDC_CLIENT_SECRET=...
WDS_OIDC_LABEL='Sign in with Entra'
```

The flow is the authorization code flow with PKCE, the callback is `/signin-oidc`, and what comes
back becomes the same three claims a password sign-in writes: the name, the role, and which
connections that account may see. Everything downstream is therefore unchanged — roles, per-account
connections, masking, and the line in the audit trail.

**The role stays the studio's own.** A provider knows its groups; it does not know what an admin may
do here.

| Variable | Meaning |
|---|---|
| `WDS_OIDC_ADMINS` | groups, roles or addresses that get the admin role |
| `WDS_OIDC_EDITORS` | the same, for `editor` |
| `WDS_OIDC_VIEWERS` | the same, for `viewer` |
| `WDS_OIDC_DEFAULT_ROLE` | what somebody who matched none of them gets, `viewer` by default |

Matching reads the `roles`, `role`, `groups` and `wids` claims and the person's own name, address and
UPN, so `WDS_OIDC_ADMINS=ada@example.com` works in a tenant with no groups. It is not case-sensitive,
because a directory is not. Admin beats editor beats viewer, so somebody in two groups gets the one
that was meant.

Which connections a provider account may see is not something a provider can know: an account that
signed in this way sees all of them, and `WDS_CONN_<NAME>_READONLY`, the production colour and the
viewer role are what narrow that. A provider *and* `WDS_USERS` can both be configured — the login
screen then shows the button and the form.

`WDS_OIDC_REQUIRE_HTTPS=false` lets a provider serve its metadata over plain http. That is for a
Keycloak on a laptop, never for a tenant on the internet. **Without it, an `http://` authority is
refused rather than used**: the studio starts, the login screen does not offer the provider, and the
log says why — a provider configured wrongly must not take the studio down with it.

The redirect URI to register with the provider is `https://<your studio>/signin-oidc`, and a provider
checks it exactly. A studio published on a port that changes between runs cannot sign anybody in, so
pin the port where the provider has to know it. Signing out signs out of the studio; the
provider still knows who you are, so signing in again may not ask twice.

## Who did what

A studio that can drop a table and export a customer list is a studio somebody will eventually be
asked questions about. The audit trail is one line per request that **changed something or took data
out of the building** — a statement run, an export, a change applied, a backup downloaded, a request
refused — with who asked, against which connection, and what came of it.

| Variable | Meaning |
|---|---|
| `WDS_AUDIT` | `false` turns the trail off, for a deployment that keeps its own record |
| `WDS_AUDIT_DAYS` | how long a line is kept, `90` by default |

The trail lives in the workspace database next to the query history and is read in the **Audit** tab
of the administration panel, which — like everything under `/api/admin` — needs the admin role. Each
line carries the route rather than the URL (`POST query/execute`, `DELETE storage`), so an action
reads the same however many tables it was aimed at, and a detail the handler chose to write down: the
statement itself for a run, the format and scope for an export.

**Bodies are never recorded.** A connection body carries a password, so what lands in the trail is
what a handler deliberately says and nothing else.

Looking at something is not written down. A schema read, a page of rows, a health report — the trail
would be nothing but those, and the question it exists to answer would be buried.

## What this is not

Masking is a guard against shoulder-surfing, screenshots and careless exports — not a substitute
for the database's own permissions. A user who may `SELECT` a column can still read it through a
connection of their own. Give the studio a database account that reaches only what it should, and
use `viewer` roles and `WDS_READONLY` where writing has no business happening.
