import { describe, expect, it } from "vitest";
import { chainSize, toChains, type LockWait } from "./blockingChains";

const wait = (blocker: string, blocked: string, waitMs = 100): LockWait =>
  ({ blocker, blocked, resource: "lock", waitMs, statement: `UPDATE from ${blocked}` });

describe("toChains", () => {
  it("nests a chain: a blocks b, b blocks c", () => {
    const chains = toChains([wait("a", "b"), wait("b", "c")]);

    const root = chains[0];
    expect(root.session).toBe("a");
    expect(root.blocked[0].session).toBe("b");
    expect(root.blocked[0].blocked[0].session).toBe("c");
  });

  it("keeps two independent pairs apart", () => {
    const chains = toChains([wait("a", "b"), wait("c", "d")]);

    expect(chains.map(chain => chain.session).sort()).toEqual(["a", "c"]);
  });

  it("hangs several blocked sessions off one blocker", () => {
    const chains = toChains([wait("a", "b"), wait("a", "c")]);

    expect(chains).toHaveLength(1);
    expect(chains[0].blocked.map(child => child.session).sort()).toEqual(["b", "c"]);
  });

  // SQL Server reports these, and a naive recursion follows them until the stack gives up.
  it("survives a cycle", () => {
    const chains = toChains([wait("a", "b"), wait("b", "a")]);

    expect(chains).toHaveLength(1);
    expect(chains[0].blocked[0].session).toBe("b");
    // And stops there rather than going round again.
    expect(chains[0].blocked[0].blocked).toHaveLength(0);
  });

  it("carries the wait time and the statement of the blocked session", () => {
    const chains = toChains([wait("a", "b", 4200)]);

    expect(chains[0].blocked[0].waitMs).toBe(4200);
    expect(chains[0].blocked[0].statement).toContain("UPDATE from b");
  });

  it("has nothing to show when nothing is blocked", () => {
    expect(toChains([])).toEqual([]);
  });
});

describe("chainSize", () => {
  it("counts everything held up below the root", () => {
    const [chain] = toChains([wait("a", "b"), wait("b", "c"), wait("a", "d")]);

    expect(chainSize(chain)).toBe(3);
  });
});
