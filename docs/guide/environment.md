# Environment variables

| Variable | Meaning |
|---|---|
| `WDS_CONNECTIONS` | JSON array of connection objects, applied at startup |
| `WDS_CONN_<NAME>` | one connection as a URL or a provider connection string; the name after the prefix becomes its label |
| `WDS_CONN_<NAME>_ENGINE` | which engine that connection string belongs to |
| `WDS_CONN_<NAME>_READONLY`, `_GROUP`, `_COLOR` | flags for the connection of the same name |
| `WDS_USER`, `WDS_PASSWORD` | when **both** are set, a login screen guards the app; the account is an admin |
| `WDS_USERS` | several accounts: `name:role:secret[:conn,conn]` entries separated by `;` — see [Safety](safety.md) |
| `WDS_TITLE` | a name for this studio, shown in the header, on the login screen and in the browser tab |
| `WDS_OIDC_AUTHORITY`, `WDS_OIDC_CLIENT_ID`, `WDS_OIDC_CLIENT_SECRET` | sign in with an identity provider instead of a list of accounts — see [Safety](safety.md#signing-in-with-an-identity-provider) |
| `WDS_OIDC_SCOPES`, `WDS_OIDC_LABEL`, `WDS_OIDC_CALLBACK_PATH`, `WDS_OIDC_REQUIRE_HTTPS` | what to ask the provider for, what the button says, where it comes back to, and whether its metadata may be plain http |
| `WDS_OIDC_ADMINS`, `WDS_OIDC_EDITORS`, `WDS_OIDC_VIEWERS`, `WDS_OIDC_DEFAULT_ROLE` | which groups, roles or addresses get which studio role, and what everybody else gets |
| `WDS_AUDIT`, `WDS_AUDIT_DAYS` | who did what through this studio, and for how long that is kept — see [Safety](safety.md#who-did-what) |
| `WDS_SECRET_KEY` | AES key (base64, 32 bytes) for stored secrets; generated into `/data/.key` if absent |
| `WDS_READONLY` | when `true`, every connection is read-only regardless of its own flag |
| `WDS_ASSIST_ENDPOINT`, `WDS_ASSIST_KEY`, `WDS_ASSIST_MODEL` | optional assistance; without the endpoint the feature does not exist — see [Optional assistance](assistant.md) |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME` | export the studio's own traces and metrics — see [Administration](administration.md) |
| `WDS_SHARE_ENABLED`, `WDS_SHARE_PUBLIC`, `WDS_SHARE_TTL_HOURS`, `WDS_SHARE_MAX_ROWS` | share a result as a link — see [Results and export](results.md) |
| `WDS_SCHEDULE_FILE`, `WDS_SCHEDULE_OUTPUT_DIR` | queries the studio runs on a schedule and writes as files — see [Results and export](results.md) |
| `WDS_SAVED_QUERIES_DIR` | folder of `.sql` files imported as saved queries at start — see [Query editor](editor.md) |
| `WDS_SEED_SQL` | a script, or a folder of `{CONNECTION}.sql`, run once per connection — see [Getting started](getting-started.md) |
| `WDS_SCHEMA_SNAPSHOT_DIR` | snapshot every connection's schema on start and report the drift — see [Schema editing](schema.md) |
| `WDS_ARCHIVE_DIR`, `WDS_ARCHIVE_MAX_ROWS` | where kept results are written, and how many rows one keeps — see [Results and export](results.md) |
| `WDS_ALERT_WEBHOOK`, `WDS_ALERT_INTERVAL_MINUTES`, `WDS_ALERT_MIN_SEVERITY`, `WDS_ALERT_CONNECTIONS` | post new health findings to a webhook — see [Administration](administration.md) |
| `WDS_MASK_EXTRA`, `WDS_MASK_NEVER`, `WDS_MASK_DEFAULT` | which columns are masked before they leave the server — see [Safety](safety.md) |
| `WDS_ASSIST_TOOLS` | `false` keeps the assistant from using the MCP tools; otherwise it uses them when both are configured |
| `WDS_MCP_ENABLED`, `WDS_MCP_PATH`, `WDS_MCP_KEY`, `WDS_MCP_ALLOW_WRITE`, `WDS_MCP_TOOLS` | serve the studio as an MCP server for AI agents — see [MCP for AI agents](mcp.md). A studio with accounts requires the key |
| `WDS_QUERY_TIMEOUT_SECONDS` | default statement timeout, default `300` |
| `WDS_MAX_ROWS` | default fetch cap per result, default `1000` |
| `WDS_STORAGE_PREVIEW_BYTES` | how much of an object the preview reads, default `65536` — see [Object storage](storage.md) |
| `WDS_STORAGE_MAX_UPLOAD_BYTES` | largest upload into a bucket, default `67108864` |
| `WDS_STORAGE_ARCHIVE_MAX_OBJECTS`, `WDS_STORAGE_ARCHIVE_MAX_BYTES` | how much of a prefix one zip may take with it — see [Object storage](storage.md) |
| `WDS_DUCKDB_EXTENSION_DIR` | where DuckDB's storage extensions are staged, `/opt/duckdb/extensions` in the image |
| `WDS_CONN_<NAME>_SCHEMAS` | read only these schemas on that connection — see [Explorer and panels](explorer.md) |
| `WDS_EXPORT_TEMPLATES_DIR` | folder of export templates the deployment ships — see [Results and export](results.md) |
| `WDS_MAX_SESSIONS` | open sessions per connection, default `8` |
| `WDS_IDLE_TIMEOUT_SECONDS` | how long an unused session stays open, default `300` |
| `WDS_OPEN_BROWSER` | `true` opens the studio on start (the default for desktop builds); `false` opens nothing |
| `WDS_APP_WINDOW` | `false` opens a plain browser tab instead of a window of its own — see [Getting started](getting-started.md#as-a-desktop-application) |
| `DB_PATH` | application SQLite database, default `/data/webdatastudio.db` |
| `ASPNETCORE_URLS` | listen address, default `http://0.0.0.0:8080` |

## Accounts

```bash
# one admin, as before
WDS_USER=admin
WDS_PASSWORD=s3cret

# or several accounts, with roles and the connections each may see
WDS_USERS='ada:admin:pbkdf2$210000$c2FsdA==$aGFzaA==;grace:viewer:pbkdf2$...:PROD'
```

Roles are `admin` (everything, including the administration surface), `editor` (read and write) and
`viewer` (every connection read-only). The optional fourth field is a whitelist of connection names
or ids; empty means all of them. The secret is either a PBKDF2 hash or a literal password. Masking
of columns that look like secrets is on by default and has no variable — it is corrected per column
from the data tab, which is described in [Safety](safety.md).

## Signing in with a provider

```bash
WDS_OIDC_AUTHORITY=https://login.microsoftonline.com/<tenant>/v2.0
WDS_OIDC_CLIENT_ID=00000000-0000-0000-0000-000000000000
WDS_OIDC_CLIENT_SECRET=...
WDS_OIDC_LABEL='Sign in with Entra'
WDS_OIDC_ADMINS=dba-group
WDS_OIDC_EDITORS=developers
```

Both the authority and the client id, or nothing: half a configuration would lock everybody out of a
studio, so it is treated as no provider at all. Configuring one also closes the door — a studio with
a provider and no `WDS_USERS` is not an open studio with a login button on it. The redirect URI to
register with the provider is `https://<your studio>/signin-oidc`.

## Connections as URLs

```bash
WDS_CONN_SHOP=postgres://app:pw@db:5432/shop
WDS_CONN_CACHE=redis://cache:6379
WDS_CONN_LOCAL=sqlite:///data/local.db
```

Recognised schemes: `postgres`, `postgresql`, `mysql`, `mariadb`, `sqlserver`, `mssql`, `sqlite`,
`oracle`, `duckdb`, `clickhouse`, `mongodb`, `redis`.

## Connections as provider connection strings

The same variable also takes the connection string a provider produces, which is what an
orchestrator such as .NET Aspire already has in hand. Name the engine alongside it:

```bash
WDS_CONN_SHOP="Host=db;Port=5432;Username=app;Password=pw;Database=shop"
WDS_CONN_SHOP_ENGINE=postgresql
WDS_CONN_SHOP_GROUP=Development
WDS_CONN_SHOP_READONLY=false
```

Without `_ENGINE` the engine is guessed from the keys in the string, and the connection is skipped
if the guess fails — better than attaching it to the wrong driver.

## Connections as JSON

```json
[{
  "name": "prod-pg",
  "engine": "postgresql",
  "connectionString": "Host=db;Port=5432;Database=shop;Username=app;Password=secret",
  "readOnly": true,
  "color": "#e03131",
  "group": "Production"
}]
```

`readOnly` is enforced in the driver, not only in the UI: a statement that is not a read is
rejected before it reaches the database. `color` tints the connection's row in the explorer, which
is the cheapest way to stop a production accident.

## Secrets

Connections added in the UI are stored in the application database with the connection string —
and an SSH private key, if there is one — encrypted with AES-GCM. The key comes from
`WDS_SECRET_KEY`, or is generated once into `/data/.key`. Keep the volume and the key together;
without the key the stored connections cannot be read back.

## Where the studio keeps its own data

`DB_PATH` has to point at local storage: SQLite needs proper file locking, which a network share —
Azure Files, NFS, SMB — does not provide. A studio whose data directory does not answer keeps
running with the connections from the environment and reports the problem in `/api/health` and in
the bottom right corner of the window, rather than failing every request that touches storage. See
[Deploying the studio](deploy.md).
