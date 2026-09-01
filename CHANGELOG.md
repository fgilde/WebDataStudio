# Changelog

What changed between releases, in the order it matters to somebody using the studio rather than in
commit order. Releases before 1.3.0 are on the
[releases page](https://github.com/fgilde/WebDataStudio/releases).

A section here is the body of that release: the release workflow reads the one the tag names
(`scripts/release-notes.mjs`) and the generated commit list follows it. So a new version is written
down here *before* it is tagged — a tag with no section still publishes, with the commit list alone.

## 1.3.0

The image, the desktop builds and the Aspire integration all come from this tag.

### Reading data

- **A pager that says which rows are on screen.** `1–200 of 12,345` rather than a highlighted page
  number, with first and last, a box to type a page number into, and rows per page (25–5000) beside
  it. Changing the size keeps the row you were looking at on screen. Where the total is the
  catalogue's estimate — PostgreSQL, SQL Server, MySQL — it says `≈`, and a filtered result says `of ?`
  rather than a number about a different set of rows; **∑** counts exactly, filter included.
- **The schemas the engine keeps for itself, on request.** `sys`, `INFORMATION_SCHEMA`, the ten fixed
  role schemas, `pg_catalog`, `performance_schema`, whatever Oracle marks as its own: hidden by
  default, one switch per connection to see them.
- **MongoDB and Redis page themselves.** A collection is a `find().sort().skip().limit()`, a key space
  is its keys with type, TTL, length and memory — server-side, not a page sorted in the browser.
- **What is actually inside a JSON column**, as the paths it holds and the `SELECT` that flattens them.
- **Timestamps on the clock you chose**, and a binary cell that is the file it holds.
- **Follow a table**: new rows arrive marked, without a full re-read.
- **What a row looked like before**, where the database kept it (system-versioned tables, flashback).

### Writing data

- **Tables with no key at all** are editable through the row address the engine gives them, with the
  refusals that keeps honest: not for a materialised view, not for a partitioned table's root.
- **Rows from the clipboard**: paste a block, see the statements, then apply.
- **A result kept as a table**, here or in another connection.
- **A file becomes a table** — CSV, NDJSON, JSON, Parquet — with the types it looks like.
- **Generated test rows** with a strategy per column and foreign keys that point at rows that exist.

### The studio around it

- **A draggable split** between editor and result, **editor zoom**, and closing tabs that leaves the
  layout as it was.
- **Look at a file instead of downloading it**: PDF, images, text, JSON, CSV, and *Save as…* where a
  download is what you wanted.
- **A folder as one zip**, and a file dropped on the tree where it belongs.
- **A data dictionary** for a whole connection, as Markdown, with the studio's own notes in it.
- **Notes on any object**, saved queries as forms, alerts that link back to what tripped them.
- **Sign in with an identity provider**, accounts and roles with every change shown as its statement,
  and an audit trail.
- **Data quality rules** that outlive a workspace, and a **development subset** of a real database —
  masked, as a script the next fresh stack can load.
- **Schema drift since the last snapshot**, as the script that carries it over.
- **Backups on a schedule**, with per-job pruning, and a database that does not start empty
  (`WithSeedFrom` in the Aspire integration).
- **Whether a server still answers**, and **what it is saying**: PostgreSQL `LISTEN`/`NOTIFY` as a
  live stream.
- **A dashboard of statements side by side**, running themselves, and what the captured minute
  suggests.
- **MCP**: the newer capabilities as tools, including `list_accounts` so an agent can answer "why can
  they read that".

### Fixed

- **A shipped preference could make every table answer 400.** A deployment that set only one
  preference sent the rest as `null`; spreading that over the defaults produced `limit=`, which every
  data tab refused.
- **Looking at a file cost the connection a session, permanently.** The download handed out a session
  per file and never took one back, so a studio with four sessions stopped answering after four
  previews — and a browser, which allows six connections per host, then looked frozen. The session now
  travels with the response body; the pool also caps how long it waits and says so instead of hanging.
- **A folder connection listed nothing in the Linux image.** Its container is a path, and a ref is
  split on the slash, so everything under it was looked for in a folder inside itself. The same
  connection worked on Windows, where a local path is spelled with backslashes.
- **`LISTEN` parked a pooled session in Npgsql's `Waiting` state**, which made the next user of that
  session — and the shutdown — fail. A listener now owns its connection and gives it up with the
  request.
- **A bucket that stopped answering is a sentence, not a spinner**: storage calls have a deadline, a
  missing container says so in one line rather than in provider XML.
- **The file viewer ran on the studio's own page** and could take its stylesheet with it; it has a
  page of its own now, and a PDF is shown rather than read as text.
- Three panels could set state after they were closed; an explorer hook ran only sometimes and took
  the tree down with it; a provider configured wrongly could take the studio down; the dashboard was
  missing from the Tools menu.

**Full changelog**: https://github.com/fgilde/WebDataStudio/compare/v1.2.0...v1.3.0
