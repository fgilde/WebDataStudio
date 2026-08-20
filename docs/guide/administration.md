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
