# Getting started

## Run the container

```bash
docker run -d --name studio -p 8080:8080 -v wds-data:/data \
  -e WDS_CONN_SHOP="postgres://app:pw@db:5432/shop" \
  ghcr.io/fgilde/webdatastudio
```

The volume holds the application database: connections you add in the UI, query history, saved
queries, snippets and layouts. Connections given as environment variables are re-read on every
start and never written there.

## Which build am I running

The version sits in the bottom right corner of the studio, and its tooltip carries the commit and
the build time. `GET /api/health` returns the same three values, which is the quicker way to tell a
pulled `:latest` from a stale local image:

```bash
curl -s http://localhost:8080/api/health
{"status":"ok","version":"1.1.42+9f3c1ab…","commit":"9f3c1ab…","built":"2026-08-19T18:57:46Z"}
```

Published images count the patch number up on their own; a version reading `1.1.0-dev` was built
by hand rather than pulled.

## With Docker Compose

```yaml
services:
  db:
    image: postgres:17
    environment:
      POSTGRES_PASSWORD: pw
      POSTGRES_DB: shop

  studio:
    image: ghcr.io/fgilde/webdatastudio
    ports: ["8080:8080"]
    volumes: ["wds-data:/data"]
    environment:
      WDS_CONN_SHOP: postgres://postgres:pw@db:5432/shop

volumes:
  wds-data:
```

## With .NET Aspire

```csharp
var db = builder.AddPostgres("db").AddDatabase("shop");

builder.AddContainer("studio", "ghcr.io/fgilde/webdatastudio")
       .WithHttpEndpoint(port: 8080, targetPort: 8080)
       .WithEnvironment("WDS_CONN_SHOP", db.Resource.ConnectionStringExpression)
       .WithVolume("wds-data", "/data");
```

### With the Aspire integration package

[Nextended.Aspire.Hosting.WebDataStudio](https://www.nuget.org/packages/Nextended.Aspire.Hosting.WebDataStudio/)
turns the whole thing into one call per database, and works out the engine from the resource:

```csharp
builder.AddPostgres("pg").AddDatabase("shop").WithWebDataStudio();
builder.AddSqlServer("sql").AddDatabase("orders").WithWebDataStudio();
builder.AddRedis("cache").WithWebDataStudio();
```

All three land in one studio. Pass `studioName:` for a second one, or build it yourself with
`AddWebDataStudio` to set a login, read-only mode and row caps from the app host.

## As a desktop application

Download the build for your platform from the
[releases page](https://github.com/fgilde/WebDataStudio/releases), unpack it and start the binary.
It serves the studio on <http://localhost:8080> and opens it **in a window of its own** — no address
bar, no tabs, an icon in the task bar like any other application. Its data lives in a `data` folder
beside the binary.

The window is a Chromium that is already on the machine: Edge, Chrome, Brave or Chromium, whichever
is found first. Nothing is bundled and nothing is downloaded for it, which is why the binary stays
one file. With none of them installed the studio opens a normal browser tab instead and says so in
its log.

| Variable | What it does |
|---|---|
| `WDS_APP_WINDOW=false` | open a normal browser tab instead of a window |
| `WDS_OPEN_BROWSER=false` | open nothing at all; visit the address yourself |

## Installing it from the browser

A studio that is already open — the container on your network, a colleague's deployment, the desktop
build — can be installed as an app without downloading anything: **Install WebDataStudio** in
Chrome's or Edge's address bar, or *Install app* in the browser menu. That is the same window without
an address bar, with its own icon, and it keeps pointing at the studio it was installed from.

Nothing is cached: the studio reads live databases, and a cached answer would be a lie about what is
in them. Installing changes how it looks, not what it knows. A studio served over plain HTTP from
something other than `localhost` cannot be installed — browsers only offer this over HTTPS.

## First query

1. Pick a connection in the explorer on the left. It expands into schemas, tables and views.
2. Press the **New query** button above the explorer, or `Ctrl+N`.
3. Type a statement and press `F5`. With text selected, only the selection runs; without a
   selection, the statement under the cursor runs.
4. The result appears below the editor while the query is still running.

## Logging in

Set `WDS_USER` and `WDS_PASSWORD` and the app asks for them once and keeps a session cookie. Leave
them unset and there is no login screen at all — the sensible default for a studio that already
sits behind your own network or proxy.

## Seeding a fresh stack

`WDS_SEED_SQL` runs a script once per connection, so a database that has just been created is worth
opening. Either one file for every connection, or a folder holding `{CONNECTION}.sql` per connection
name:

```bash
WDS_SEED_SQL=/data/seed          # SHOP.sql, WAREHOUSE.sql, …
WDS_SEED_SQL=/data/seed.sql      # or one for all of them
```

This is for development stacks, and it has three rules so it cannot become a foot-gun:

- **Once per content.** The script's hash is remembered, so restarting does not insert everything
  again — and editing the script does make it run again.
- **Never on a read-only connection**, and never on one marked as production (colour red). A red
  connection is somebody saying "not here".
- **One transaction** where the engine has them: half a seed is worse than none. A script that fails
  is not remembered as done, so fixing it and restarting runs it.
