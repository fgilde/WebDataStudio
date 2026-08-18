import { describe, expect, it } from "vitest";
import { flattenGroups, groupRows } from "./grouping";

describe("groupRows", () => {
  const rows: unknown[][] = [
    ["london", 10],
    ["berlin", 5],
    ["london", 7],
    [null, 3],
  ];

  it("groups by a column value with counts", () => {
    const groups = groupRows(rows, 0);
    expect(groups.map(g => g.label).sort()).toEqual(["(null)", "berlin", "london"]);
    expect(groups.find(g => g.label === "london")!.count).toBe(2);
  });

  it("sums the numeric columns per group", () => {
    const groups = groupRows(rows, 0);
    expect(groups.find(g => g.label === "london")!.subtotals[1]).toBe(17);
  });

  it("gives nulls their own group", () => {
    expect(groupRows(rows, 0).find(g => g.label === "(null)")!.count).toBe(1);
  });

  it("handles a column of unique values", () => {
    const unique: unknown[][] = [["a", 1], ["b", 2], ["c", 3]];
    expect(groupRows(unique, 0)).toHaveLength(3);
  });

  it("leaves a non-numeric column out of the subtotals", () => {
    const groups = groupRows([["a", "text"], ["a", "more"]], 0);
    expect(groups[0].subtotals[1]).toBeUndefined();
  });

  it("flattens into header rows followed by their rows", () => {
    const flat = flattenGroups(groupRows([["a", 1], ["a", 2]], 0));
    expect(flat).toHaveLength(3);
    expect("group" in flat[0]).toBe(true);
    expect("row" in flat[1]).toBe(true);
  });

  it("groups an empty result into nothing", () => {
    expect(groupRows([], 0)).toEqual([]);
  });
});
