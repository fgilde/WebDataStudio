# Redis

Redis is not SQL, and a studio that treats it as a table with two columns is useless for it. The
command console in a query tab is one half — this is the other: a keyspace to walk, values edited in
the shape they actually have, and the views that answer why it is slow or large.

Open it from the key icon in the explorer's toolbar; the icon only appears for a Redis connection.

## Keys

The keyspace is walked with `SCAN`, one page at a time, with the cursor the server handed back — a
million keys neither loads nor blocks, and "load more" continues from where the last page ended. The
pattern box (`user:*`) and the type filter are passed to the server, and each key shows its type, its
memory (`MEMORY USAGE`) and its remaining life.

`KEYS` is never used. It is the command that turns "let me look at this" into an incident.

## Values

One editor per type, because a hash is not a list is not a stream:

| Type | What you get |
|---|---|
| string | text, JSON (pretty-printed) or a hex dump, detected from the value |
| hash | field/value table, edit in place, add and remove fields |
| list | index and value, push left or right, remove by value |
| set | members, add and remove |
| sorted set | score and member, both editable |
| stream | entries with their ids, append an entry, and the consumer groups below |

Every write shows the Redis commands it will run and waits for you:

```
HSET profile:1 city berlin
```

That is not decoration. Redis has no transaction to roll back afterwards, so the preview is the last
place a mistake can be caught. A read-only connection refuses the write in the driver, not by hiding
the button.

TTL is next to the value: set one in seconds, or take it off with "remove expiry".

## Deleting or expiring a pattern

"Delete matching…" and "Expire matching…" work on whatever the current pattern selects. The preview
counts the keys and shows the first twenty; applying acts on **that** set, resolved when you looked
at it — a key created between the preview and the apply is not touched, because it was not part of
what you approved.

A pattern matching more than 100 000 keys is refused: at that point the pattern is the problem.

## Analysis

Sampled, not exhaustive — the point is to find the prefix that grew:

- **Memory by prefix**, which is how every Redis codebase names its keys, with a bar per prefix.
- **Types**, with the count and memory per type.
- **The largest keys**, which is usually where the answer is.
- **What expires soonest**, and how much of the sample has no expiry at all.

The header shows the server's own totals (`DBSIZE`, `used_memory`) next to the sample size, so a
sampled answer is never mistaken for the whole picture.

## Pub/Sub

Subscribe to patterns and watch messages arrive live — the studio holds the subscription open as
server-sent events for as long as the panel is. Publishing is right there too, which is the fastest
way to check that a consumer is listening at all. The list keeps the newest 500 messages; a busy
channel would otherwise grow until the tab dies.

## Streams and consumer groups

A stream's entries are in the value editor; its groups are underneath, with the number of consumers,
the pending count and the last delivered id. Pending entries — delivered but never acknowledged — are
what "stuck" looks like, and the panel says how long the oldest has been idle.

## Slow log

`SLOWLOG GET`, with the duration and the command. The threshold is the server's own
`slowlog-log-slower-than`, so an empty list means nothing was slower than that — not that the studio
cannot see it.

## Command help in the console

A Redis connection's query tab is a command console, and it completes from what **this** server
reports: `COMMAND DOCS` for the summary and the group, `COMMAND` for the arity, merged. A server with
modules therefore completes its module commands too — `JSON.SET` is in the list if the server has it,
and absent if it does not, which a list baked into the studio could never get right.

Completion offers commands for the first word of a line only. The rest are keys and arguments, and
guessing those is not the studio's business. Hovering a command shows its summary, group, arity and
the version it appeared in.

## Cluster

The **Cluster** tab reads `CLUSTER INFO` and `CLUSTER NODES`: the state, the known nodes, each
node's endpoint, role, slot range and link state.

A standalone server — the common case — reports itself as one master node holding all of the
keyspace, rather than failing. Everything else the studio shows for that connection comes from that
one server, and the tab says so.
