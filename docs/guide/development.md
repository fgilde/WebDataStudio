# Development

## Layout

```
src/WebDataStudio.Server   .NET 10 minimal API: drivers, endpoints, export, analysis
web                        React 19 + Mantine + dockview + Monaco
tests                      xunit v3, live databases through Testcontainers
docs                       this site and the feature matrix
```

## Running both halves

```bash
# API on :5000
ASPNETCORE_URLS=http://localhost:5000 DB_PATH=/tmp/wds.db \
  dotnet run --project src/WebDataStudio.Server

# SPA on :5173, proxying /api to :5000
cd web && npm install && npm run dev
```

The published container serves the built SPA from the same origin, so there is no CORS
configuration anywhere.

## Tests

```bash
dotnet test                      # server; starts real databases in containers
cd web && npx vitest run         # SPA units
cd web && npm run smoke          # browser check against a running server
cd web && npm run smoke:admin    # diagram, administration and comparison panels
cd web && npm run smoke:p9       # palette, saved queries, builder, charts, parameters
cd web && npm run smoke:mcp      # the MCP endpoint and its dialog (needs WDS_MCP_ENABLED=true)
cd web && npm run smoke:objects  # policies, partitions, a function run, preferences, snapshots
cd web && npm run smoke:dbgate   # the filter language, archives, perspective, the map
```

The server suite runs one behaviour suite against every engine fixture, so a driver that is added
inherits the whole contract. A separate test asserts capability honesty: whatever a driver declares
unsupported has to throw rather than quietly do nothing.

## Adding an engine

1. Implement `IDbDriver`, usually by deriving from `AdoDriverBase`.
2. Declare a `DriverCapabilities` that tells the truth.
3. Add a fixture to the contract suite; that is where most of the work shows up.
4. Add the engine to `ConnectionRegistry.KnownEngines` and to the URL scheme map.

## Building the image

```bash
docker build -t webdatastudio:dev .
docker run --rm -p 8080:8080 webdatastudio:dev
```

`scripts/verify-backup-roundtrip.sh` runs that image against a live PostgreSQL and round-trips a
dump through the backup and restore endpoints.

## Documentation

The site under `docs/` is plain HTML plus docsify; no build step. `web/scripts/screenshots.mjs`
regenerates the screenshots in a dark and a light theme against a running server.
