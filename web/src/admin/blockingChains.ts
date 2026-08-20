/// One session waiting for another, as the server reports it.
export interface LockWait {
  blocker: string;
  blocked: string;
  resource: string;
  waitMs: number;
  statement: string | null;
}

export interface ChainNode {
  session: string;
  statement?: string | null;
  resource?: string;
  waitMs: number;
  blocked: ChainNode[];
}

/// Turns a flat list of "A blocks B" into the trees it describes. A list answers "who is waiting";
/// the tree answers "who to kill", which is the session at the root.
///
/// Two things a real server does that a naive build breaks on: the same session can block several
/// others, and SQL Server can report a cycle (A waits for B, B waits for A) — recursion has to stop
/// rather than run out of stack in the middle of an incident.
export function toChains(waits: LockWait[]): ChainNode[] {
  const blockedBy = new Map<string, LockWait[]>();
  const blockedSessions = new Set<string>();

  for (const wait of waits) {
    const list = blockedBy.get(wait.blocker) ?? [];
    list.push(wait);
    blockedBy.set(wait.blocker, list);
    blockedSessions.add(wait.blocked);
  }

  const build = (session: string, seen: Set<string>): ChainNode => {
    const children = blockedBy.get(session) ?? [];

    return {
      session,
      waitMs: 0,
      blocked: children
        .filter(child => !seen.has(child.blocked))
        .map(child => ({
          ...build(child.blocked, new Set([...seen, child.blocked])),
          statement: child.statement,
          resource: child.resource,
          waitMs: child.waitMs,
        })),
    };
  };

  // A root is a session that blocks somebody without being blocked itself. In a cycle nothing
  // qualifies, so the first blocker is taken as the root — a cycle still has to be visible.
  const roots = [...blockedBy.keys()].filter(session => !blockedSessions.has(session));
  const starts = roots.length > 0 ? roots : [...blockedBy.keys()].slice(0, 1);

  return starts.map(session => build(session, new Set([session])));
}

/// How many sessions a chain holds up, root excluded. That number is what decides which chain to
/// deal with first.
export const chainSize = (node: ChainNode): number =>
  node.blocked.reduce((total, child) => total + 1 + chainSize(child), 0);
