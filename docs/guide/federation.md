# Joining across connections

Two databases that know nothing about each other, one query. Each source query runs where it lives;
its rows are copied into an in-memory DuckDB table named by its alias, and the federated query runs
over those tables.

The copying is the honest part of this: the studio does not pretend two databases are one. It says
how much it would copy, and refuses when that would be too much.

Open it from the explorer's toolbar (*Join across connections*) or the command palette.

## A source

| Field | Meaning |
|---|---|
| Connection | where the query runs |
| Alias | the table name the rows get in the federated query |
| SQL | anything that connection understands, returning rows |

An alias has to be a plain identifier — letters, digits and underscores. Two sources cannot share
one.

## The federated query

Plain SQL, run by DuckDB, over the aliases:

```sql
SELECT r.label, count(*) AS people
  FROM c JOIN r ON r.code = c.region
 GROUP BY r.label
 ORDER BY r.label
```

*What would be staged* shows the `CREATE TABLE` per source without copying a row — the fastest way
to catch a typo in an alias or a broken source query.

## Limits, and why they exist

- **100 000 rows per source** by default, changeable per run. Above that, staging stops being a
  query and starts being an import; a source that exceeds it is refused **by name** rather than
  filling the server's memory.
- **Types are mapped narrowly.** Numbers stay numbers so a join and a `SUM` behave, dates stay
  dates so ordering does, and everything else is staged as text.
- **Masked columns stay masked.** A federated query is another way into the same data, so the mask
  policy of each source applies on the way through — see [Safety](safety.md).
- **Nothing is written.** The staged tables live in one DuckDB connection for the length of the
  request and are gone with it. The federated query cannot change either source.

## When not to use it

If both tables live in the same database, join them there — the engine has indexes and statistics,
and staging throws both away. Federation is for the case where they genuinely do not.
