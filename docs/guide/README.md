# WebDataStudio

A database studio that runs in your browser. One container serves the whole studio: connections,
a query editor, editable results, schema tools, execution plans, comparison and administration.

```bash
docker run -d -p 8080:8080 -v wds-data:/data \
  -e WDS_CONN_LOCAL="postgres://app:pw@db:5432/shop" \
  ghcr.io/fgilde/webdatastudio
```

Open <http://localhost:8080>. Without `WDS_USER` and `WDS_PASSWORD` there is no login screen.

![Query editor and result grid](../assets/screenshots/query-dark.png)

## Where to start

- [Getting started](getting-started.md) — run it, attach a database, run the first query.
- [Environment variables](environment.md) — everything the container reads at startup.
- [Engine capabilities](engines.md) — what each of the nine engines supports.

## What it is not

WebDataStudio does not replace your migration tool, your monitoring stack or your backup schedule.
It is the place where a person looks at a database and changes something on purpose — with the
statement shown before it runs.
