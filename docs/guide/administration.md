# Administration

![Administration](../assets/screenshots/admin-dark.png)

## Maintenance

A catalogue of commands per engine: `VACUUM`, `ANALYZE`, `REINDEX` on PostgreSQL, `OPTIMIZE`,
`CHECK`, `FLUSH` on MySQL, `DBCC CHECKDB` and index rebuilds on SQL Server, and so on. The
destructive ones are marked and ask before they run.

The endpoint takes a command id from that catalogue, never raw SQL, and quotes the target through
the dialect — so this panel cannot become a second, unlogged query console.

## Sessions

The session list shows who is connected, what they are running, how long it has taken and who is
blocking whom. A session can be terminated after a confirmation that shows its current statement.

## Databases

List, create and drop databases on the engines that have more than one. Dropping asks you to type
the name.

## Users and privileges

List the users, and create one or grant a privilege through the same preview-then-apply handshake
the rest of the app uses: the statement is shown, and only then does it run.

## Server log

Shown where the engine exposes it through SQL. Where it does not, the panel says which engine and
why instead of showing an empty box.

## Backup and restore

Backups run the engine's own tool — `pg_dump`, `mysqldump`, `mongodump`, `redis-cli --rdb` — and
stream the result straight to your browser. SQLite copies itself with `VACUUM INTO`; SQL Server
writes a `.bak` on the database server and reports the path.

Passwords are handed to those tools through the environment, never as a command-line argument that
every process on the machine could read.

Restore uploads a dump and asks you to type the target database's name first. It is the one action
in the app that overwrites a whole database.

## Overview

The first tab answers the question the other eight could only answer between them: connections,
cache hit ratio, how many sessions are waiting, how many statements are running, how long the
longest has been going, and the size of the database. Each tile keeps the last five minutes of
readings, so a number that is climbing looks different from one that is merely high.

Below the tiles, everything the server is working on. PostgreSQL and SQL Server report a percentage
for a vacuum or an index build; MySQL and Oracle report the statement and its age, which is the
useful half. Nothing running says so instead of showing an empty table.

### Who is blocking whom

When sessions are waiting, the overview shows the chains rather than a list of waiters: the session
at the root is the one holding everything up, and it is the one with the kill button. Killing a
waiter frees nothing, so that button is only offered where it can help. A cycle — which SQL Server
does report — is shown once rather than followed round for ever.

## Replication

Replicas, their state and their lag, in bytes and in seconds where the engine reports both. A server
with no replicas says so, and so does one whose account cannot read the replication view — those are
different problems and it matters which one you have.

## Sizes

The databases tab draws the sizes as a treemap above the list. A hundred databases are one glance
here and a hundred lines to scroll in a table; the list is still underneath for the actions.

## Applying a recommendation

The health report names its fixes — `CREATE INDEX`, `DROP INDEX`, `VACUUM (ANALYZE)`. Each one has an
**Apply this…** button that runs it through the same preview-and-confirm path the table designer
uses: the script, whether it is destructive, and one place where it goes into the database. A
recommendation nobody can act on is a recommendation nobody acts on.

## Alerts

The studio runs the analysis behind its health report on a timer and posts what is **new** to a
webhook, so somebody hears about a missing index without opening the studio first.

| Variable | Meaning |
|---|---|
| `WDS_ALERT_WEBHOOK` | the URL to post to; without it nothing is watched |
| `WDS_ALERT_INTERVAL_MINUTES` | how often to look, default `60` |
| `WDS_ALERT_MIN_SEVERITY` | `info`, `warning` (default) or `critical` |
| `WDS_ALERT_CONNECTIONS` | connection names to watch, comma-separated. Empty means all |

```bash
WDS_ALERT_WEBHOOK=https://hooks.slack.com/services/…
WDS_ALERT_INTERVAL_MINUTES=120
```

The payload carries a `text` field — what Slack, Mattermost, Discord and Teams render — and the
findings themselves, each with the statement that would fix it:

```json
{
  "text": "*SHOP* — 2 new health findings\n• [warning] events has no primary key",
  "connection": { "id": "env-…", "name": "SHOP", "engine": "sqlite" },
  "findings": [{ "category": "no-primary-key", "severity": "warning", "title": "…", "fix": null }]
}
```

Only new findings are sent: the same warning every hour is a message people learn to ignore. A post
that fails is retried on the next sweep rather than swallowed, and a connection that cannot be
reached is a log line rather than an alert — whatever watches uptime already knows.

## Traces and metrics

With `OTEL_EXPORTER_OTLP_ENDPOINT` set — the standard variable, so a stack that already exports
telemetry needs nothing studio-specific — the studio reports its own work to that collector:

| What | Name |
|---|---|
| span per run | `query.execute`, tagged with the engine, the rows and whether it failed |
| span per tool call | `mcp.{tool}` |
| statements run | `wds.queries` (counter, tagged by engine and outcome) |
| how long they took | `wds.query.duration` (histogram, ms) |
| rows handed to a client | `wds.rows` (counter) |
| MCP tool calls | `wds.tool.calls` (counter, tagged by tool and outcome) |

ASP.NET Core, HttpClient and .NET runtime instrumentation ride along. Health checks are filtered out
of the traces — they would be most of the traffic and none of the information.

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:4317
OTEL_SERVICE_NAME=analytics-studio      # defaults to "webdatastudio"
```

Nothing is exported without that endpoint. The instrumentation is always compiled in, which costs
nothing while nobody listens — and means a `dotnet-counters` or a test can watch it locally without
a collector at all.
