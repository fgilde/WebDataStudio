# Environment variables

| Variable | Meaning |
|---|---|
| `WDS_CONNECTIONS` | JSON array of connection objects, applied at startup |
| `WDS_CONN_<NAME>` | one connection as a URL; the name after the prefix becomes its label |
| `WDS_USER`, `WDS_PASSWORD` | when **both** are set, a login screen guards the app |
| `WDS_SECRET_KEY` | AES key (base64, 32 bytes) for stored secrets; generated into `/data/.key` if absent |
| `WDS_READONLY` | when `true`, every connection is read-only regardless of its own flag |
| `WDS_QUERY_TIMEOUT_SECONDS` | default statement timeout, default `300` |
| `WDS_MAX_ROWS` | default fetch cap per result, default `1000` |
| `WDS_MAX_SESSIONS` | open sessions per connection, default `8` |
| `WDS_IDLE_TIMEOUT_SECONDS` | how long an unused session stays open, default `300` |
| `WDS_OPEN_BROWSER` | `true` opens a browser on start (the default for desktop builds) |
| `DB_PATH` | application SQLite database, default `/data/webdatastudio.db` |
| `ASPNETCORE_URLS` | listen address, default `http://0.0.0.0:8080` |

## Connections as URLs

```bash
WDS_CONN_SHOP=postgres://app:pw@db:5432/shop
WDS_CONN_CACHE=redis://cache:6379
WDS_CONN_LOCAL=sqlite:///data/local.db
```

Recognised schemes: `postgres`, `postgresql`, `mysql`, `mariadb`, `sqlserver`, `mssql`, `sqlite`,
`oracle`, `duckdb`, `clickhouse`, `mongodb`, `redis`.

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
