import { describe, expect, it } from "vitest";
import { describeDiff, diffRows } from "./diffRows";

describe("diffRows", () => {
  it("marks a changed cell and says which one", () => {
    const previous = [[1, "ada", 10], [2, "grace", 20]];
    const next = [[1, "ada", 11], [2, "grace", 20]];

    const diff = diffRows(previous, next, [0]);

    expect(diff.flags).toEqual(["changed", "same"]);
    expect([...diff.cells]).toEqual(["0:2"]);
    expect(diff.removed).toEqual([]);
  });

  it("marks a new key as added", () => {
    const diff = diffRows([[1, "ada"]], [[1, "ada"], [2, "linus"]], [0]);

    expect(diff.flags).toEqual(["same", "added"]);
    expect(diff.cells.size).toBe(0);
  });

  it("reports a key that vanished", () => {
    const diff = diffRows([[1, "ada"], [2, "linus"]], [[1, "ada"]], [0]);

    expect(diff.flags).toEqual(["same"]);
    expect(diff.removed).toEqual([[2, "linus"]]);
  });

  it("calls identical data the same", () => {
    const rows = [[1, "ada"], [2, "linus"]];
    const diff = diffRows(rows, rows.map(r => [...r]), [0]);

    expect(diff.flags).toEqual(["same", "same"]);
    expect(describeDiff(diff)).toBe("no change");
  });

  // A row that moved is still the same row; only a query without a key has to fall back to position.
  it("follows a row that moved when there is a key", () => {
    const diff = diffRows([[1, "ada"], [2, "linus"]], [[2, "linus"], [1, "ada"]], [0]);

    expect(diff.flags).toEqual(["same", "same"]);
  });

  it("compares by position without key columns", () => {
    const diff = diffRows([[1, "ada"], [2, "linus"]], [[2, "linus"], [1, "ada"]]);

    expect(diff.flags).toEqual(["changed", "changed"]);
    expect(diff.removed).toEqual([]);
  });

  it("treats a shorter result as rows gone, without a key", () => {
    const diff = diffRows([[1], [2], [3]], [[1], [2]]);

    expect(diff.flags).toEqual(["same", "same"]);
    expect(diff.removed).toEqual([[3]]);
  });

  // null and "null" must not look like the same value, and neither must 1 and "1"… but a driver may
  // return either for the same column between runs, so the comparison is deliberately textual and
  // says so. What matters is that a real change is never missed.
  it("sees a value becoming null", () => {
    const diff = diffRows([[1, "ada"]], [[1, null]], [0]);

    expect(diff.flags).toEqual(["changed"]);
    expect([...diff.cells]).toEqual(["0:1"]);
  });

  it("describes a mixed run", () => {
    const diff = diffRows([[1, "a"], [2, "b"], [3, "c"]], [[1, "z"], [3, "c"], [4, "d"]], [0]);

    expect(describeDiff(diff)).toBe("1 changed, 1 added, 1 gone");
  });

  it("handles a composite key", () => {
    const previous = [[1, "eu", 5], [1, "us", 7]];
    const next = [[1, "eu", 5], [1, "us", 8]];

    const diff = diffRows(previous, next, [0, 1]);

    expect(diff.flags).toEqual(["same", "changed"]);
  });
});
