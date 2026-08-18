# WebDataStudio

A web-based database management studio — the things DbGate, DataGrip, SQL Server Management Studio
and phpMyAdmin do, in one container, for as many engines as possible. The UI follows
[AspireUI](https://github.com/fgilde/AspireUI): same themes, same dockview layouts, same feel.

> Status: **P0 (skeleton)**. Connections, authentication and the shell work. Database drivers,
> the query editor and everything downstream arrive in the phases listed in
> [`docs/superpowers/plans`](docs/superpowers/plans/README.md).

## Run it

```bash
docker run -d -p 8080:8080 -v wds-data:/data \
  -e WDS_CONN_LOCAL="postgres://app:pw@db:5432/shop" \
  ghcr.io/fgilde/webdatastudio
```

Open <http://localhost:8080>. Without `WDS_USER` and `WDS_PASSWORD` there is **no login screen** —
the app opens straight into the studio.

## Environment variables

| Variable | Meaning |
|---|---|
| `WDS_CONNECTIONS` | JSON array of connection objects, applied at startup |
| `WDS_CONN_<NAME>` | one connection as a URL, e.g. `postgres://user:pw@host:5432/db` |
| `WDS_USER`, `WDS_PASSWORD` | when **both** are set, a login screen guards the app; otherwise anonymous |
| `WDS_SECRET_KEY` | AES key (base64, 32 bytes) for stored connection secrets; generated into `/data/.key` if absent |
| `DB_PATH` | application SQLite database, default `/data/webdatastudio.db` |
| `WDS_QUERY_TIMEOUT_SECONDS` | default statement timeout, default 300 |
| `WDS_MAX_ROWS` | default fetch cap per result, default 1000 |
| `WDS_READONLY` | when `true`, every connection is read-only regardless of its own flag |
| `ASPNETCORE_URLS` | listen address, default `http://0.0.0.0:8080` |

`WDS_CONNECTIONS` entry shape:

```json
[{
  "name": "prod-pg",
  "engine": "postgresql",
  "connectionString": "Host=db;Port=5432;Database=shop;Username=app;Password=secret",
  "readOnly": true,
  "color": "red",
  "group": "Production"
}]
```

URL schemes map to engines: `postgres`, `postgresql`, `mysql`, `mariadb`, `sqlserver`, `mssql`,
`sqlite`, `oracle`, `duckdb`, `clickhouse`, `mongodb`, `redis`.

Connections defined in the environment are re-read on every start, are read-only in the UI, and
carry a badge. Connections added in the UI live in `/data` with their passwords encrypted at rest.

## Develop

```bash
# API on :5000
ASPNETCORE_URLS=http://localhost:5000 DB_PATH=/tmp/wds.db dotnet run --project src/WebDataStudio.Server

# SPA on :5173, proxying /api to :5000
cd web && npm install && npm run dev
```

Tests:

```bash
dotnet test          # server
cd web && npx vitest run   # SPA
```

## Documentation

- [Design spec](docs/superpowers/specs/2026-08-18-webdatastudio-design.md) — architecture, API, and
  the full 96-item feature inventory.
- [Implementation plans](docs/superpowers/plans/README.md) — phases P0 through P9.
