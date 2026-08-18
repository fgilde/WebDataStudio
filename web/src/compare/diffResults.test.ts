import { describe, expect, it } from "vitest";
import { diffResults, type ResultSet } from "./diffResults";

const set = (rows: unknown[][]): ResultSet => ({ columns: ["id", "name"], rows });

describe("diffResults", () => {
  it("reports nothing for identical results", () => {
    const diff = diffResults(set([[1, "ada"]]), set([[1, "ada"]]), ["id"]);
    expect(diff.different).toEqual([]);
    expect(diff.onlyInA).toEqual([]);
    expect(diff.onlyInB).toEqual([]);
    expect(diff.identical).toBe(1);
  });

  it("names the column that changed", () => {
    const diff = diffResults(set([[1, "ada"]]), set([[1, "ada-2"]]), ["id"]);
    expect(diff.different).toHaveLength(1);
    expect(diff.different[0].changedColumns).toEqual(["name"]);
    expect(diff.different[0].key).toEqual([1]);
  });

  it("sorts rows present on one side only into the right bucket", () => {
    const diff = diffResults(set([[1, "ada"], [2, "linus"]]), set([[2, "linus"], [3, "grace"]]), ["id"]);
    expect(diff.onlyInA).toEqual([[1, "ada"]]);
    expect(diff.onlyInB).toEqual([[3, "grace"]]);
    expect(diff.identical).toBe(1);
  });

  it("matches on a composite key", () => {
    const a: ResultSet = { columns: ["x", "y", "v"], rows: [[1, 1, "a"], [1, 2, "b"]] };
    const b: ResultSet = { columns: ["x", "y", "v"], rows: [[1, 1, "a"], [1, 2, "changed"]] };

    const diff = diffResults(a, b, ["x", "y"]);
    expect(diff.identical).toBe(1);
    expect(diff.different[0].key).toEqual([1, 2]);
  });

  it("treats a number and its text form as the same value", () => {
    expect(diffResults(set([[1, "ada"]]), set([["1", "ada"]]), ["id"]).identical).toBe(1);
  });

  it("refuses a comparison without key columns", () => {
    expect(() => diffResults(set([]), set([]), [])).toThrow(/key column/i);
  });

  it("says which key column is missing", () => {
    expect(() => diffResults(set([]), set([]), ["nope"])).toThrow(/nope/);
  });
});
