# Connections

## Adding one in the UI

Open **Connections** in the header, press **Add**, and either fill in the form or paste a
connection string — pasting detects the engine and fills the rest. **Test** opens the connection
once and reports what the server said, without saving anything.

Both shapes are taken: the provider-native string,
`Host=db;Port=5432;Database=shop;Username=app;Password=pw`, and the URL,
`postgres://app:pw@db:5432/shop` — which the studio translates into the first before any driver sees
it. A password with a `#`, a `?` or a space in it needs no encoding by hand in either.

## A database file

Not every database is a server. A SQLite file, a DuckDB file or a folder of Parquet and CSV files is
a database too, and the **Add** form takes them two ways:

- **Upload** — pick the file in the file dialog. The studio keeps it under its own data directory,
  in a folder named after the connection, and opens it from there. This is the way in for the file
  on the laptop in front of you, which the container cannot see.
- **Browse the server** — walk the folders the studio is allowed to read and pick a file that is
  already there. This is the way in for a mounted share. The folders are the studio's own data
  directory plus whatever `WDS_FILE_ROOTS` names; nothing outside them is reachable.

What opens what:

| Extension | Opened as | Notes |
| --- | --- | --- |
| `.db`, `.sqlite`, `.sqlite3`, `.db3`, `.s3db` | SQLite | The file has to start with `SQLite format 3` — a `.db` is whatever somebody renamed |
| `.duckdb`, `.ddb` | DuckDB | |
| `.parquet`, `.csv`, `.tsv`, `.ndjson`, `.jsonl`, `.json`, `.xlsx` (also `.gz`, `.zst`) | A storage connection over the folder the file lies in | Read-only, always |

A data file is not a fourth engine: the studio already reads a folder of files through DuckDB, and
one file is a folder with one file in it. That connection cannot be written through, whatever the
rest of the settings say.

Two formats are refused, with the reason rather than a shrug:

- `.mdf` and `.ldf` — a SQL Server data file cannot be opened on its own. It needs a running SQL
  Server to attach it; attach it there and add that server as a connection.
- `.accdb` and `.mdb` — Access needs the ACE driver, which exists only on Windows and only as an
  install of its own.

## A link that opens a connection

A deployment can let the studio open connections named in its own URL, which turns it into
something like a live viewer for databases: send somebody a link, and the database is open when the
page finishes loading.

```
https://studio.example/?u=/data/shop.sqlite3
https://studio.example/?u=sales:/data/sales.duckdb,orders:/mnt/exports/orders.parquet
https://studio.example/?u=https://data.example/shop.sqlite3
https://studio.example/?u=shop:postgres%3A%2F%2Freader%3Apw%40db%3A5432%2Fshop
```

One `?u=` holds a comma-separated list. Each entry is a path, an `http(s)` URL or a whole
connection string, optionally with `label:` in front to name the connection. An entry that carries
commas or semicolons of its own — a connection string, `Server=host,1433` — has to be
percent-encoded, or the comma splits it.

It is **off by default**. `WDS_OPEN_FROM_URL` says what a link may carry:

| Value | What is allowed |
| --- | --- |
| `false` (default) | Nothing. `?u=` is ignored, with one line saying which setting would allow it |
| `true` | A path and a download — never a connection string |
| `file` | A path inside the allowed roots |
| `download` | An `http(s)` URL, and only from the hosts `WDS_OPEN_FROM_URL_HOSTS` names |
| `connection-string` | A whole connection string, credentials and all |
| `file,download` | Several of them, comma-separated |

`connection-string` has to be named on purpose, and `true` deliberately leaves it out: a connection
string in a URL is a password in browser history, in proxy logs and in screenshots.

A download is fetched once into the studio's data directory and then opened as a file. It needs
`WDS_OPEN_FROM_URL_HOSTS` — a fetcher that takes its address from a link is a way to reach the
addresses only the server can — and it stops at `WDS_OPEN_FROM_URL_MAX_MB`, keeping nothing when a
file runs past it. Redirects are not followed: a redirect is a second host.

What a link opens belongs to the browser that opened it. It is marked **from a link** in the tree,
nobody else sees it, and it is gone when the process restarts — nothing about it is written down,
which is the point when the link carried a password. `WDS_OPEN_FROM_URL_KEEP=store` is the other
branch: the connection is written to the store like any other, kept across restarts and visible to
everybody. Right for a studio one person runs, wrong for a shared one.

Connections opened this way are read-only unless `WDS_OPEN_FROM_URL_WRITABLE=true`. Each entry
answers for itself: a link with three databases in it opens the two it may and says in one line why
the third stayed closed. The `u` parameter is dropped from the address bar as soon as the studio has
handed it over.

## The object tree

A connection expands into schemas, then folders, then objects — and an object expands one level
further into its columns, indexes, foreign keys and triggers, each with its type or the columns it
covers next to the name.

Right-click gives the menu that fits what you clicked, and nothing else: a table offers data,
design, indexes, scripts and export; a column offers a query on it, an index over it and a
`DROP COLUMN` script; an index offers a rebuild and a drop; a folder offers a new table and a
refresh. Anything the engine cannot do is left out rather than shown broken.

Destructive statements are written into a query tab instead of running from the menu. Creating a
database, creating a table and changing an index are the exceptions — they have their own dialog,
and every schema change still shows its SQL before it runs.

### Azure SQL, Synapse and Fabric

The form's **Start from** list carries the connection strings nobody remembers: Azure SQL with a
managed identity, with your own account, or with an Entra password; a Synapse serverless or dedicated
pool; a Fabric warehouse; the Azure database services. A preset fills the connection string in and
can be edited afterwards like any other.

Where the connection says a person signs in — `Authentication="Active Directory Device Code Flow"` or
`Interactive` — the studio does not try to open a browser inside its container. It runs the device-code
flow itself: the connection list marks the connection with a **sign-in** badge, the key icon opens a
dialog with a code, and you enter that on any device that has a browser. The token then stays in
memory on the server, never on disk and never in the browser, and the connection opens with it until it
expires.

A managed identity needs none of this and remains the better answer wherever it exists.

A bucket is a connection too: `s3://`, `azblob://`, `gs://` and `file://` open object storage in
the same tree, where a file can be queried as a table — see [Object storage](storage.md).

## A studio anybody brings their own data to

Everything above assumes one kind of deployment: somebody writes the connections down, a team opens
the studio, everybody sees the same databases. There is another — a studio on the open internet as a
viewer, where every visitor brings their own database and sees nobody else's. Two settings make that
one, and neither changes anything unless it is set.

**Where does a new connection go?** `WDS_CONNECTION_SCOPE`:

| Value | What it means |
| --- | --- |
| `stored` (default) | The connection store: written down, kept across restarts, everybody who may see it does |
| `session` | The browser that made it, and nobody else. Nothing on disk, gone when the studio restarts or the session expires |

In `session` scope the form, an upload, an import and a `?u=` link all end up in the same place: this
browser's own list. The connection list says so, and offers **Forget my connections** — one button
for everything you brought, files included.

**Which ways in exist at all?** Three switches, all on unless a deployment says otherwise:

| Variable | The door it closes |
| --- | --- |
| `WDS_ALLOW_ADD_CONNECTION=false` | The form. Also importing, and testing a connection — that one opens whatever it is given and keeps nothing, which makes it the cheapest door of the four |
| `WDS_ALLOW_FILE_UPLOAD=false` | A database file from the visitor's machine |
| `WDS_ALLOW_FILE_BROWSE=false` | Walking the server's own folders |

A closed door takes its button with it rather than showing one that answers with a refusal, and the
refusal — for anybody who asks the API directly — names the setting that would allow it.

"Nobody may add anything" is those three set to `false`. That is worth having on its own: a
deployment whose connections come from an app host can close the form and keep everything else.

### A session that ends

`WDS_SESSION_TTL_MINUTES` (240 by default, `0` meaning never) is how long a session may go quiet
before its connections and the files behind them are dropped; a sweeper runs every five minutes.
`WDS_SESSION_MAX_CONNECTIONS` (25) is how many one browser may hold at once.

The cookie that separates visitors is `HttpOnly` and `Secure` over https. It is not a security
boundary: somebody who copies another person's cookie gets that person's connections.

### The hosts the studio may reach

A studio anybody may type a connection string into is an outbound connector from wherever it runs: a
visitor can reach addresses only the server can reach. `WDS_CONNECT_HOSTS` — empty by default, so no
restriction — is a comma-separated allow-list every connection target is checked against, wherever
the connection came from: the form, a test, a link, the store, the environment. `*.example.com`
matches one level of subdomain, the same rule the download list uses.

A connection whose host is outside the list is not offered at all, including one written down before
the list was set — a connection from the environment must not become the way around it. A connection
string no host can be read from is refused while the list is set, because guessing would make the
list a suggestion. Set it on anything a stranger can reach; see [Deploying](deploy.md#exposure).

## Properties

**Properties…** on a connection, its database or a schema opens what that connection is: the name,
engine, where it was defined, whether it is read-only, the SSH tunnel if there is one — and what
the server itself reports, such as its version, the current database, encoding, time zone and
size. The bottom of the dialog lists what the engine supports, so a missing button in the UI has a
visible reason.

The connection string is shown there too, with the password replaced by a mask. The eye reveals it
and there are two copy buttons: one copies the string without the password, the other with it. The
password is fetched only when one of those is pressed — it is never part of a routine page load,
and revealing it does not survive closing the dialog.

If the server does not answer, the dialog still shows the definition and says what went wrong;
that is often exactly what it was opened to find out.

## Groups and colours

A connection can carry a group and a colour. The explorer draws groups as collapsible headers and
tints each connection's row with its colour. Red for production is a convention worth adopting.

## Read-only connections

The read-only flag is checked in the driver: anything that is not a read is refused with a clear
message, and the UI hides the actions that would fail. `WDS_READONLY=true` forces it for every
connection at once.

## SSH tunnels

Open the **SSH tunnel** section of the connection form and give it a host, a user and either a
password or a private key. WebDataStudio opens the tunnel when a session needs it, shares one
tunnel across concurrent sessions, and closes it a minute after the last one ends.

The database host and port in the connection string stay as the jump host sees them — that is the
point of a tunnel. If the tunnel cannot be opened, the error names SSH rather than reporting a
generic timeout against a host you could never reach directly.

## TLS

The **TLS** section writes the right key into the connection string for the engine you picked:
`SSL Mode` for PostgreSQL, `SslMode` for MySQL, `Encrypt` for SQL Server. Client certificates are
referenced by path in the connection string, so the files have to be reachable by the container.

## Import and export

`GET /api/connections/export` returns the definitions without any secret: no connection string, no
password, no key. Importing that file recreates the connections with the host and database filled
in and the credentials empty, so a shared file cannot leak a password.

## Pooling

Sessions are pooled per connection. `WDS_MAX_SESSIONS` caps how many a single connection may hold
at once, and `WDS_IDLE_TIMEOUT_SECONDS` decides when an unused one is closed. Editing or deleting
a connection drops its pooled sessions immediately.
