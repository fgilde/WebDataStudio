import { describe, expect, it } from "vitest";
import { flattenDocuments, isFlat } from "./flattenDocuments";

describe("flattenDocuments", () => {
  it("uses the first document for column order", () => {
    const table = flattenDocuments([{ id: 1, name: "ada" }, { id: 2, name: "linus" }]);
    expect(table.columns).toEqual(["id", "name"]);
    expect(table.rows).toEqual([[1, "ada"], [2, "linus"]]);
  });

  it("fills a missing key with null instead of shifting the row", () => {
    const table = flattenDocuments([{ id: 1, name: "ada" }, { id: 2 }]);
    expect(table.rows[1]).toEqual([2, null]);
  });

  it("adds a key that only a later document has", () => {
    const table = flattenDocuments([{ id: 1 }, { id: 2, extra: true }]);
    expect(table.columns).toEqual(["id", "extra"]);
    expect(table.rows[0]).toEqual([1, null]);
  });

  it("keeps a nested object as a value", () => {
    const table = flattenDocuments([{ id: 1, meta: { a: 1 } }]);
    expect(table.rows[0][1]).toEqual({ a: 1 });
  });

  it("wraps scalars in a single value column", () => {
    expect(flattenDocuments(["hello", "world"])).toEqual({
      columns: ["value"], rows: [["hello"], ["world"]],
    });
  });

  it("handles an empty list", () => {
    expect(flattenDocuments([])).toEqual({ columns: ["value"], rows: [] });
  });
});

describe("isFlat", () => {
  it("is true for shallow documents", () =>
    expect(isFlat([{ a: 1, b: "x" }, { a: 2, b: null }])).toBe(true));

  it("is false when a value is an object", () =>
    expect(isFlat([{ a: { b: 1 } }])).toBe(false));

  it("is false when a value is an array", () =>
    expect(isFlat([{ a: [1, 2] }])).toBe(false));
});
