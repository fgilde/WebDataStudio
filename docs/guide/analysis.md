# Analysis

## Execution plans

The **Plan** panel explains the statement in the active query tab, estimated or actual. The plan is
both a tree and a graph, with a heat map over the cost so the expensive node is the one you see
first. Sequential scans on large tables, missing indexes and spills to disk are called out.

## Index advisor

From a statement and its plan the advisor proposes concrete `CREATE INDEX` statements with the
reason it thinks each would help. It reads the predicates, not just the table names, and it says
when it is guessing from the SQL alone because no plan was available.

## Deep analyze

The **Health** panel walks a whole schema and reports missing indexes, unused indexes, duplicate
indexes, unindexed foreign keys, table bloat and stale statistics — with the statement to fix each
finding where there is one.

## Statistics and metrics

Table statistics — size, row count, index size, last vacuum or analyze — sit in the object detail
panel. Server-wide metrics, blocking chains and the slow-query list live in the **Administration**
panel, for the engines that expose them: `pg_stat_statements` on PostgreSQL, the Query Store on SQL
Server, `performance_schema` on MySQL.

If the source is not installed, the panel says so rather than showing an empty table.

## Trying an index rather than trusting one

An advisor claims; a trial measures. Where a finding is a `CREATE INDEX`, **Try it** creates that
index under a name of the studio's own making, asks the engine for the plan again, and drops it:

> cheaper by 96%, and it stopped scanning the table
> Seq Scan → Index Scan, cost 1000 → 40

"The planner would probably use it" is the part everybody gets wrong, which is the whole reason this
exists. The answer names the operations as well as the cost, because an engine that reports no cost
at all still says whether it stopped reading the table.

Not in a transaction, on purpose: MySQL and Oracle commit DDL whatever a transaction says, so a
rollback would be a promise that holds on some engines and not others. Creating and dropping is the
same shape everywhere — and where the drop fails, the answer says which index is still there.

Refused on a read-only connection and on one marked as production. Building an index takes locks and
time on the real table, and "it was only a trial" is no comfort at 3am.
