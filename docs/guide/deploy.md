# Deploying the studio

The container runs anywhere, but two things behave differently in the cloud than on your machine:
where its own data lives, and how it authenticates to a managed database.

## Its own storage

The studio keeps connections you add in the UI, query history, saved queries, snippets and layouts
in a SQLite file at `DB_PATH` (default `/data/webdatastudio.db`). SQLite needs a file system that
does file locking properly, which rules out network shares — an SMB or NFS mount, and that includes
an **Azure Files** volume on Azure Container Apps, either crawls or blocks outright.

Point `DB_PATH` at local storage: a real disk, a persistent volume claim backed by a block device,
or nothing at all — the container's own file system works fine if you accept that a restart starts
over. Connections defined through `WDS_CONN_*` are re-read on every start and are unaffected either
way.

`GET /api/health` tells you which state the studio is in:

```json
{
  "status": "degraded",
  "version": "1.1.42+9f3c1ab",
  "store": {
    "path": "/data/webdatastudio.db",
    "available": false,
    "error": "'/data/webdatastudio.db' did not answer within 10 seconds…"
  },
  "connections": 2
}
```

A studio in that state stays usable for everything that comes from the environment — it just cannot
save anything, and it says so in the corner of the window. It is a `degraded`, not a crash, on
purpose: an unreachable data directory used to hang the first request that touched it and queue
every later one behind it.

Set `WDS_SECRET_KEY` (base64, 32 bytes) wherever storage is persistent. Without it the encryption
key is generated next to the database, and replacing the volume makes the stored connections
unreadable.

## Entra authentication for Azure SQL

A connection string of the shape

```
Server=tcp:my-server.database.windows.net,1433;Encrypt=True;Authentication="Active Directory Default";Database=shop
```

works as it stands. The studio resolves the credential from its environment — on Azure Container
Apps and App Service that is the managed identity behind `AZURE_CLIENT_ID`, locally it is whatever
the Azure CLI is logged in as. What the database still needs is a user for that identity:

```sql
CREATE USER [my-identity] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [my-identity];   -- db_owner if the studio may write
```

With [Nextended.Aspire.Hosting.WebDataStudio](https://www.nuget.org/packages/Nextended.Aspire.Hosting.WebDataStudio)
that user, the identity and the Key Vault access for secret-backed connection strings are generated
for you.

## Behind a reverse proxy

Object references contain a slash — `Table:dbo/AbpUsers` — and every API call that addresses an
object passes it as a query value (`?ref=…`) rather than a path segment. That is not cosmetic: the
proxy in front of a deployed studio normalises the path first, and Envoy on Azure Container Apps
turns `%2F` back into a real slash, which used to leave the request matching no route at all. Data
tabs, the structure panel and the index dialog answered 404 in the cloud while working locally.

Nothing to configure — but if you put your own proxy in front of the studio, do not strip or rewrite
query strings.

## Exposure

There is no login screen unless `WDS_USER` and `WDS_PASSWORD` are set, which is the right default
for a studio bound to your own machine and the wrong one for anything reachable from outside. A
public studio without credentials hands every visitor whatever the connections behind it can do —
so set them, and consider `WDS_READONLY=true`, which is enforced in the driver rather than by
hiding buttons.

`WDS_OPEN_FROM_URL` is off for the same reason. A link that carries a connection string carries a
password — into browser history, proxy logs and screenshots — which is why `true` allows a path and
a download but never `connection-string`; that one has to be named on purpose. A download also
needs `WDS_OPEN_FROM_URL_HOSTS`: a studio that fetches whatever address a link names is a way for a
visitor to reach the addresses only the server can.

### A studio anybody may use

A viewer on the open internet is a deployment of its own: no accounts, no connections, and every
visitor brings their own database.

```yaml
environment:
  # What a visitor makes belongs to their browser and is written down nowhere.
  WDS_CONNECTION_SCOPE: session
  # A connection string and a file from their machine; the server's own folders stay private.
  WDS_ALLOW_ADD_CONNECTION: "true"
  WDS_ALLOW_FILE_UPLOAD: "true"
  WDS_ALLOW_FILE_BROWSE: "false"
  # A link that opens a database: ?u=… — never a connection string, which `true` leaves out anyway.
  WDS_OPEN_FROM_URL: "true"
  WDS_OPEN_FROM_URL_HOSTS: data.example
  # Nothing is written through, and a session does not outlive the afternoon.
  WDS_READONLY: "true"
  WDS_SESSION_TTL_MINUTES: "120"
  WDS_UPLOAD_MAX_MB: "50"
  # Where the studio may connect at all. The one line not to leave out.
  WDS_CONNECT_HOSTS: "*.example.com"
```

Two things to be honest about. Without `WDS_CONNECT_HOSTS` a visitor who may type a connection
string can reach whatever the container can reach, which on most networks is more than you meant to
offer — put the studio somewhere with restricted egress as well, because a list is a list and a
network is a boundary. And there is no rate limiting: a public studio can be used to hammer a
database somebody else owns, so put it behind whatever your proxy offers for that.

## Which build is running

The version sits in the bottom right corner of the window, and its tooltip carries the commit and
build time; `/api/health` returns the same three values.

- A version ending in `-dev` (`1.8.0-dev`) was built by hand rather than pulled.
- An image from `master` is the last release plus the build number: `1.8.0.125` is the 125th
  published build, made after 1.8.0 — newer than the release, not the release itself.
- A **tagged** build takes its version from the tag, so the download on the releases page, the image
  tagged the same way and the number the studio shows itself are one and the same. `v1.1.0` is
  `1.1.0` everywhere.
