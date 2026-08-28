import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  beginTransaction, commitTransaction, rollbackTransaction, openTransactions, heldFor,
} from "./transaction";

const answer = (body: unknown, ok = true) =>
  Promise.resolve({
    ok,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body)),
  } as Response);

describe("holding a transaction open", () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => vi.unstubAllGlobals());

  it("begins one on a connection and gets its id back", async () => {
    fetchMock.mockReturnValue(answer({ id: "t1", connectionId: "c1", statements: 0 }));

    const open = await beginTransaction("c1");

    expect(open.id).toBe("t1");
    expect(fetchMock).toHaveBeenCalledWith("/api/tx/begin", expect.objectContaining({
      method: "POST",
      body: JSON.stringify({ connectionId: "c1" }),
    }));
  });

  it("commits and rolls back by id", async () => {
    fetchMock.mockReturnValue(answer({ committed: true }));
    await commitTransaction("t1");
    expect(fetchMock).toHaveBeenCalledWith("/api/tx/t1/commit", expect.anything());

    fetchMock.mockReturnValue(answer({ rolledBack: true }));
    await rollbackTransaction("t1");
    expect(fetchMock).toHaveBeenCalledWith("/api/tx/t1/rollback", expect.anything());
  });

  /// A transaction that timed out is gone, and the message says which of the three it was.
  it("passes the server's own words through when one is gone", async () => {
    fetchMock.mockReturnValue(answer({ message: "this transaction is not open any more" }, false));

    await expect(commitTransaction("t1")).rejects.toThrow(/not open any more/);
  });

  it("lists what is open with the idle timeout", async () => {
    fetchMock.mockReturnValue(answer({ idleTimeoutSeconds: 900, open: [{ id: "t1" }] }));

    const state = await openTransactions();

    expect(state.idleTimeoutSeconds).toBe(900);
    expect(state.open).toHaveLength(1);
  });
});

describe("how long it has been open", () => {
  const now = new Date("2026-08-29T12:00:00Z").getTime();
  const ago = (seconds: number) => new Date(now - seconds * 1000).toISOString();

  it("counts in the words somebody would use", () => {
    expect(heldFor(ago(5), now)).toBe("5s");
    expect(heldFor(ago(95), now)).toBe("1m 35s");
    expect(heldFor(ago(3700), now)).toBe("1h 1m");
  });

  it("never counts backwards on a clock that disagrees", () => {
    expect(heldFor(ago(-30), now)).toBe("0s");
  });
});
