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

## As a desktop application

Download the build for your platform from the
[releases page](https://github.com/fgilde/WebDataStudio/releases), unpack it and start the binary.
It serves the studio on <http://localhost:8080>, opens your browser and stores its data in a
`data` folder beside the binary.

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
