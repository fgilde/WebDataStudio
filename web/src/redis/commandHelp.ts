import { redisCommands, type RedisCommandDto } from "../api";

export interface CommandHelp {
  name: string;
  arity: number;
  summary: string;
  group: string;
  since: string;
}

/// The help for one server, by command name. Built from what the server itself reports, so a
/// server with modules gets its own commands too.
export type CommandIndex = Map<string, CommandHelp>;

export const indexCommands = (commands: RedisCommandDto[]): CommandIndex =>
  new Map(commands.map(c => [c.name.toUpperCase(), {
    name: c.name.toUpperCase(),
    arity: c.arity ?? 0,
    summary: c.summary ?? "",
    group: c.group ?? "",
    since: c.since ?? "",
  }]));

/// What to offer for a prefix. Commands that start with it come first, in name order; a container
/// command's subcommands (`CLIENT LIST`) are matched on the whole typed line, which is why the
/// prefix may contain a space.
export function suggest(index: CommandIndex, prefix: string, limit = 50): CommandHelp[] {
  const needle = prefix.trim().toUpperCase();
  if (needle.length === 0) return [...index.values()].slice(0, limit);

  const starts: CommandHelp[] = [];
  const contains: CommandHelp[] = [];

  for (const help of index.values()) {
    if (help.name.startsWith(needle)) starts.push(help);
    else if (help.name.includes(needle)) contains.push(help);
  }

  const byName = (a: CommandHelp, b: CommandHelp) => a.name.localeCompare(b.name);
  return [...starts.sort(byName), ...contains.sort(byName)].slice(0, limit);
}

/// The one-line hover: what it does, and what it costs to call it wrong.
export function describe(help: CommandHelp): string {
  const parts = [
    help.summary,
    help.group ? `group: ${help.group}` : "",
    help.arity !== 0 ? `arity: ${help.arity}` : "",
    help.since ? `since: ${help.since}` : "",
  ].filter(Boolean);

  return parts.join(" · ");
}

// One index per connection for the lifetime of the page: the answer changes when the server does,
// not while somebody is typing.
const cache = new Map<string, Promise<CommandIndex>>();

export function commandIndex(connectionId: string): Promise<CommandIndex> {
  const existing = cache.get(connectionId);
  if (existing) return existing;

  const loading = redisCommands(connectionId)
    .then(indexCommands)
    .catch(() => new Map<string, CommandHelp>());

  cache.set(connectionId, loading);
  return loading;
}
