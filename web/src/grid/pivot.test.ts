import { describe, it, expect } from "vitest";
import { pivot, MAX_COLUMNS } from "./pivot";

const columns = [{ name: "status" }, { name: "month" }, { name: "total" }];

const rows: unknown[][] = [
  ["paid", "2026-01", 10],
  ["paid", "2026-01", 30],
  ["paid", "2026-02", 5],
  ["open", "2026-02", 7],
  ["open", "2026-01", null],
];

describe("crossing two columns", () => {
  it("counts what falls in each cell", () => {
    const result = pivot(columns, rows, { row: "status", column: "month", value: "", aggregate: "count" });

    expect(result.columns).toEqual(["2026-01", "2026-02"]);
    expect(result.rows.map(r => r.key)).toEqual(["open", "paid"]);

    const paid = result.rows.find(r => r.key === "paid")!;
    expect(paid.cells).toEqual([2, 1]);
    expect(paid.total).toBe(3);

    expect(result.totals).toEqual([3, 2]);
    expect(result.grand).toBe(5);
  });

  it("sums, averages and picks the extremes of a value column", () => {
    const sum = pivot(columns, rows, { row: "status", column: "month", value: "total", aggregate: "sum" });
    expect(sum.rows.find(r => r.key === "paid")!.cells).toEqual([40, 5]);

    const average = pivot(columns, rows,
      { row: "status", column: "month", value: "total", aggregate: "avg" });
    expect(average.rows.find(r => r.key === "paid")!.cells).toEqual([20, 5]);

    const smallest = pivot(columns, rows,
      { row: "status", column: "month", value: "total", aggregate: "min" });
    expect(smallest.rows.find(r => r.key === "paid")!.cells).toEqual([10, 5]);
  });

  /// An average that folded nulls in as zero would be a lie. The cell that had no number at all is
  /// empty instead.
  it("leaves out values that are not numbers rather than counting them as zero", () => {
    const result = pivot(columns, rows, { row: "status", column: "month", value: "total", aggregate: "avg" });
    const open = result.rows.find(r => r.key === "open")!;

    expect(open.cells[0]).toBeNull();   // 2026-01 held only a null
    expect(open.cells[1]).toBe(7);
  });

  it("gives null a name, because grouping by it is a real question", () => {
    const result = pivot([{ name: "city" }], [["london"], [null]],
      { row: "city", column: "", value: "", aggregate: "count" });

    expect(result.rows.map(r => r.key)).toEqual(["london", "(none)"]);
  });

  it("sorts numbers as numbers", () => {
    const result = pivot([{ name: "n" }], [[2], [10], [1]],
      { row: "n", column: "", value: "", aggregate: "count" });

    expect(result.rows.map(r => r.key)).toEqual(["1", "2", "10"]);
  });

  it("without a column field it is one total per row", () => {
    const result = pivot(columns, rows, { row: "status", column: "", value: "", aggregate: "count" });

    expect(result.columns).toEqual([""]);
    expect(result.rows.find(r => r.key === "paid")!.total).toBe(3);
  });

  /// A pivot with nine hundred columns is a scroll bar, not an answer.
  it("stops at a readable number of columns and says it did", () => {
    const many: unknown[][] = Array.from({ length: MAX_COLUMNS + 5 },
      (_, index) => ["one", `c${index}`, 1]);

    const result = pivot(columns, many, { row: "status", column: "month", value: "", aggregate: "count" });

    expect(result.columns).toHaveLength(MAX_COLUMNS);
    expect(result.truncated).toBe(true);
  });

  it("answers with nothing when the row column is not in the result", () => {
    const result = pivot(columns, rows, { row: "nope", column: "month", value: "", aggregate: "count" });

    expect(result.rows).toHaveLength(0);
    expect(result.grand).toBeNull();
  });
});
