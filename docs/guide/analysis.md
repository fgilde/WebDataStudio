# Analysis

## Execution plans

The **Plan** panel draws the plan the way SSMS does: right to left, from the tables to the result,
every operator with its share of the statement's cost, its rows (actual of estimated when the plan
ran) and its time, and arrows as thick as the rows they carry. Click an operator for every property
the server reported — predicates, output columns, the runtime counters of each thread. The search
box finds an operator or a table; Enter jumps to the next one.

On SQL Server the whole plan is there, including memory grant, waits, warnings and the indexes the
server asks for. **Save** writes it as `.sqlplan` or `.xml`, which SSMS and Rider open. **Open plan**
(the folder button, *Open execution plan* in the command palette, or a file dropped on the panel)
reads such a file back — from SSMS, from a colleague, from last week — into its own tab, without a
connection.

Estimated or actual: an actual plan runs the statement. Sequential scans on large tables, missing
indexes and spills to disk are called out under **Findings**.

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
