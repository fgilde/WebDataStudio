# Engine capabilities

Every driver declares what its engine supports, and the UI hides the rest. A driver that claims a
capability has to implement it — a test asserts that anything declared unsupported throws instead
of silently doing nothing.

| Capability | PostgreSQL | MySQL | SQL Server | SQLite | Oracle | DuckDB | ClickHouse | MongoDB | Redis | Storage |
|---|---|---|---|---|---|---|---|---|---|---|
| SQL | yes | yes | yes | yes | yes | yes | yes | — | — | yes |
| Browse as rows | yes | yes | yes | yes | yes | yes | yes | yes | yes | yes |
| Browse a container as rows | — | — | — | — | — | — | — | — | yes | — |
| Count a column's values | yes | yes | yes | yes | yes | yes | yes | — | — | yes |
| Schemas | yes | — | yes | — | yes | yes | yes | — | — | — |
| Several databases | yes | yes | yes | — | — | — | yes | yes | yes | — |
| Transactions | yes | yes | yes | yes | yes | yes | — | — | — | — |
| DDL | yes | yes | yes | yes | yes | yes | yes | — | — | — |
| Views | yes | yes | yes | yes | yes | yes | yes | — | — | — |
| Materialised views | yes | — | — | — | yes | — | yes | — | — | — |
| Stored procedures | yes | yes | yes | — | yes | — | — | — | — | — |
| Triggers | yes | yes | yes | yes | yes | — | — | — | — | — |
| Sequences | yes | — | yes | — | yes | yes | — | — | — | — |
| Foreign keys | yes | yes | yes | yes | yes | yes | — | — | — | — |
| Partial indexes | yes | — | yes | yes | — | — | — | — | — | — |
| Include columns | yes | — | yes | — | — | — | — | — | — | — |
| Estimated plan | yes | yes | yes | yes | yes | yes | yes | yes | — | yes |
| Actual plan | yes | yes | yes | — | — | yes | — | yes | — | yes |
| Backup | yes | yes | yes | yes | — | — | — | yes | yes | — |
| Restore | yes | yes | — | — | — | — | — | yes | — | — |
| User management | yes | yes | yes | — | yes | — | — | — | — | — |
| Session list | yes | yes | yes | — | yes | — | yes | yes | yes | — |
| Kill session | yes | yes | yes | — | yes | — | yes | yes | yes | — |
| Server metrics | yes | yes | yes | — | yes | — | yes | yes | yes | — |
| Slow queries | yes | yes | yes | — | — | — | — | — | — | — |
| Scheduled jobs | yes | yes | yes | — | — | — | — | — | — | — |
| Maintenance commands | yes | yes | yes | yes | yes | yes | yes | yes | yes | — |

MongoDB and Redis are not SQL engines: their query tabs take the engines' own commands, and results
that are documents render as a JSON tree with a table view for flat ones.

They still browse. The data tab asks the driver for a page rather than building a `SELECT`, so a
MongoDB collection is read with `find().sort().skip().limit()` — including the studio's filter
language, translated into the query — and a Redis database, prefix folder or single key is read as
the table it makes. Two things follow from having no SQL: a grid over them is read-only, and it
says which command writes instead; and counting a column's values (the tick-list in the column
menu) is refused, because that is a `GROUP BY`. Type the filter instead.

"Browse a container as rows" is the second column-level difference: for every SQL engine a schema
is a folder and nothing else, while a Redis database or key prefix is itself the interesting table
— its keys, their types, their expiry and their size.

DuckDB and SQLite are files, so they have no sessions, no users and no second database to switch
to. SQLite backs itself up with `VACUUM INTO`; restoring means replacing the file, which the app
deliberately does not do underneath an open connection.

Storage is a bucket rather than a database — an S3-compatible endpoint, Azure Blob Storage, Google
Cloud Storage, or a folder. It has no schemas, no keys and nothing to write DDL against, so a file is
read and queried and never edited row by row. Reading happens through DuckDB, which is also where the
plan comes from. One stated limit: DuckDB reaches Google Cloud Storage over the S3 protocol, which
wants HMAC keys — with a service account alone the tree, the preview and the download all work and a
query does not. See [Object storage](storage.md).
