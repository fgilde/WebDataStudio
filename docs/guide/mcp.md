# The studio as an MCP server

The studio can hand its databases to an AI agent over the [Model Context
Protocol](https://modelcontextprotocol.io): Claude Code, Claude Desktop, VS Code, Cursor — anything
that speaks MCP. The agent gets the same deal a person gets, which is the only reason this is safe
to switch on.

Off by default. Without configuration the route does not exist, and `/api/health` reports no `mcp`
at all.

## Turning it on

| Variable | Meaning |
|---|---|
| `WDS_MCP_ENABLED` | `true` serves MCP |
| `WDS_MCP_PATH` | where, default `/mcp`. Setting a path alone also switches the feature on |
| `WDS_MCP_KEY` | required from the client as `Authorization: Bearer …` (or `X-API-Key`) |
| `WDS_MCP_ALLOW_WRITE` | `true` lets the agent change data, through a preview and its hash |

```bash
WDS_MCP_ENABLED=true
WDS_MCP_KEY=a-long-random-string
```

**A studio with accounts needs a key.** The MCP endpoint sits outside the login screen — an agent
has no cookie — so on a studio with `WDS_USER`/`WDS_USERS` it refuses to start without
`WDS_MCP_KEY` and says so in the log. Without that rule it would be a way past the login.

The header carries a plug icon once the endpoint is live. It opens a dialog with the URL and
ready-to-paste configuration for the common clients, plus the list of tools the endpoint offers. The
icon shows an orange dot when writing is allowed.

## Connecting a client

```bash
# Claude Code
claude mcp add --transport http webdatastudio http://localhost:8080/mcp \
  --header "Authorization: Bearer $WDS_MCP_KEY"
```

```json
// Claude Desktop, VS Code, Cursor — the same three fields under their own key
{
  "mcpServers": {
      "webdatastudio": {
      "type": "http",
      "url": "http://localhost:8080/mcp",
      "headers": { "Authorization": "Bearer …" }
    }
  }
}
```

A `GET` on the path describes the server in plain JSON, which is the fastest way to check a
deployment from a terminal.

## The tools

| Tool | What it does |
|---|---|
| `list_connections` | the databases this studio can reach, with their ids |
| `list_tables` | every table and view of a connection in one call — the usual first question |
| `list_objects` | walks the object tree a level at a time, for a database too large to list |
| `describe_object` | columns, indexes, foreign keys, triggers, row count, and which columns are masked |
| `browse_rows` | a page of rows from a table, view, collection or key space — sortable and filterable, masked and capped |
| `run_query` | one **reading** statement, masked and capped |
| `preview_script` | splits a script, marks the destructive statements, returns a hash. Nothing runs |
| `explain_plan` | the query plan for a statement — why it is slow, without guessing |
| `health_report` | the studio's own analysis, each finding with the statement that fixes it |
| `server_activity` | what is running, and who is waiting on whom |
| `redis_value` | one Redis key, in the shape its type has |
| `find_data` | looks for a value in every text column of every table — where does this customer number actually live |
| `json_shape` | what is inside a JSON column: which paths, how often, which types, and the SELECT that flattens them |
| `table_sizes` | how big every table is, and how much bigger than it was, with a per-day rate |
| `query_stats` | what this studio has run, grouped by shape, and whether it is getting slower |
| `inspect_sql` | reads a statement without running it: a DELETE with no WHERE, a cartesian join, a NOT IN that a NULL will break |
| `profile_table` | what a table actually holds, counted — and which columns look like they hold something personal |
| `object_notes` | the notes people left on an object in this studio |
| `quality_rules` | the rules somebody wrote about this connection's data |
| `run_quality_rules` | runs them and says how many rows break each one |
| `preview_script` | splits a script, marks the destructive statements, returns a hash. Nothing runs |
| `apply_script` | runs the script that hash belongs to |
| `save_quality_rule` | writes a rule about the data, so what was found once is watched from then on |
| `add_note` | leaves a note on an object, so what was worked out once is next to the thing it is about |

`preview_script`, `apply_script`, `save_quality_rule` and `add_note` exist only when
`WDS_MCP_ALLOW_WRITE=true`; otherwise they are not in `tools/list` at all, and calling them by name
says why. The last two change the studio's own state rather than the database, but they are writes and
are treated as ones. A note left by an agent is signed `mcp`, because a note from an agent should not
read as though a person wrote it.

**MongoDB and Redis need no separate tools either.** `browse_rows` asks the driver for the page, so
a collection is read with a `find`, a Redis database answers with its keys and their types and a Redis
key with its own contents. Along with `limit` and `offset` it takes `sort`, `desc`, `filterColumn` and
`filter` — the same [filter language](results.md#the-filter-language) a person types — and answers
with the total where the engine can say it cheaply, plus a `note` for anything it could not do.
`redis_value` is still there for one key's value with its nesting intact.

**A bucket needs no separate tools.** [Object storage](storage.md) is a connection like any other, so
`list_tables` lists its objects, `describe_object` describes a Parquet file's columns, and
`browse_rows` reads it through the reader that opens it — `read_parquet`, `read_csv_auto`,
`read_json_auto` — rather than as a table with that name.

**What is deliberately not offered.** The audit trail is for the people who run the studio, not for an
agent, and the [development subset](explorer.md#a-development-subset) hands out a file full of rows —
both stay where a person has to ask for them.

## Narrowing the endpoint

`WDS_MCP_TOOLS` names the tools the endpoint offers, comma-separated:

```bash
WDS_MCP_TOOLS=list_connections,list_tables,describe_object,explain_plan
```

It is a whitelist, so a tool added in a later version does not appear on an endpoint somebody
deliberately narrowed — and a tool left out is refused by name when it is called, rather than being
silently absent.

`explain_plan` with `actual: "true"` runs the statement to measure it, so it obeys the same rule
`run_query` does: a write is refused.

## What an agent cannot do

- **Write without asking twice.** `run_query` refuses anything that is not a read — and refuses a
  reading statement with a second statement behind it, which is how that guard is usually walked
  around. Writing goes through `preview_script` (which runs nothing) and then `apply_script` with
  the hash it returned. The hash is consumed, so the same call cannot run twice by accident.
- **See a masked column.** The mask policy is the studio's, not the caller's: `api_key` comes back
  as dots for an agent exactly as it does for a person, and `describe_object` says which columns
  those are so the agent does not report the dots as a value.
- **Reach a read-only connection.** `WDS_READONLY`, a connection's own read-only flag and a
  `viewer` account's read-only rule all still apply.
- **Page without limit.** A tool call returns at most 200 rows.

## The studio's own assistant, with tools

When both the MCP endpoint and the [assistant](assistant.md) are configured, the assistant uses the
same tools: the dialog grows an **Answer it from the database** button, and the model looks things
up instead of guessing. The answer names the tools it used, so it can be checked rather than
believed.

`WDS_ASSIST_TOOLS=false` keeps the assistant explaining and drafting only.

With `WDS_MCP_ALLOW_WRITE=true` the assistant can also change data — through the same preview and
hash, and it is told to say what the script does before applying it. That is a real, working "do it
for me": worth having, and worth pointing at a copy of the database first.

## Deployments

The endpoint is HTTP on the same port as the studio, so anything that reaches the studio reaches
it. On a published studio: give it a key, keep `WDS_MCP_ALLOW_WRITE` off unless an agent really
should be writing, and remember that a red (production) connection still refuses to export
unmasked secrets — see [Safety](safety.md).

From an Aspire app host, one call does all of it:

```csharp
builder.AddWebDataStudio()
    .WithReference(shop)
    .WithMcpEndpoint(apiKey: mcpKey)          // Nextended.Aspire.Hosting.WebDataStudio
    .WithClaudeAssistant(anthropicKey);
```
