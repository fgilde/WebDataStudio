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
| `WDS_SECRET_KEY` | AES key (base64, 32 bytes) for stored secrets; generated into `/data/.key` if absent |
| `WDS_READONLY` | when `true`, every connection is read-only regardless of its own flag |
| `WDS_ASSIST_ENDPOINT`, `WDS_ASSIST_KEY`, `WDS_ASSIST_MODEL` | optional assistance; without the endpoint the feature does not exist — see [Optional assistance](assistant.md) |
| `WDS_ASSIST_TOOLS` | `false` keeps the assistant from using the MCP tools; otherwise it uses them when both are configured |
| `WDS_MCP_ENABLED`, `WDS_MCP_PATH`, `WDS_MCP_KEY`, `WDS_MCP_ALLOW_WRITE` | serve the studio as an MCP server for AI agents — see [MCP for AI agents](mcp.md). A studio with accounts requires the key |
| `WDS_QUERY_TIMEOUT_SECONDS` | default statement timeout, default `300` |
| `WDS_MAX_ROWS` | default fetch cap per result, default `1000` |
| `WDS_MAX_SESSIONS` | open sessions per connection, default `8` |
| `WDS_IDLE_TIMEOUT_SECONDS` | how long an unused session stays open, default `300` |
| `WDS_OPEN_BROWSER` | `true` opens a browser on start (the default for desktop builds) |
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
