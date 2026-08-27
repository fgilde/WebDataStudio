# Connections

## Adding one in the UI

Open **Connections** in the header, press **Add**, and either fill in the form or paste a
connection string — pasting detects the engine and fills the rest. **Test** opens the connection
once and reports what the server said, without saving anything.

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
