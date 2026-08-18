import { describe, expect, it } from "vitest";
import { heightOf, layout } from "./layout";
import type { DiagramEdgeDto, DiagramNodeDto } from "../api";

const table = (name: string, columns = 2): DiagramNodeDto => ({
  id: name,
  schema: "public",
  name,
  columns: Array.from({ length: columns }, (_, i) => ({
    name: `c${i}`, type: "int", nullable: true, primaryKey: i === 0, foreignKey: false,
  })),
});

const edge = (source: string, target: string): DiagramEdgeDto => ({
  name: `fk_${source}_${target}`, source, target,
  sourceColumns: ["c1"], targetColumns: ["c0"], resolved: true,
});

describe("layout", () => {
  it("places every table exactly once", () => {
    const placed = layout([table("a"), table("b")], []);
    expect(placed.map(p => p.id).sort()).toEqual(["a", "b"]);
  });

  it("puts the referenced table left of the referencing one", () => {
    const placed = layout([table("orders"), table("customers")], [edge("customers", "orders")]);
    const customers = placed.find(p => p.id === "customers")!;
    const orders = placed.find(p => p.id === "orders")!;

    expect(customers.x).toBeLessThan(orders.x);
  });

  it("ignores an edge whose other side is not drawn", () => {
    // A schema filter can cut off the referenced table; dagre must not invent a box for it.
    const placed = layout([table("orders")], [edge("orders", "elsewhere")]);
    expect(placed).toHaveLength(1);
  });

  it("survives a self-referencing foreign key", () => {
    // A tree table pointing at its own parent column is a normal schema, not a cycle bug.
    const placed = layout([table("categories")], [edge("categories", "categories")]);
    expect(placed).toHaveLength(1);
    expect(Number.isFinite(placed[0].x)).toBe(true);
  });

  it("grows the box with the column count but caps it", () => {
    expect(heightOf(table("a", 3))).toBeLessThan(heightOf(table("a", 10)));
    expect(heightOf(table("a", 40))).toEqual(heightOf(table("a", 18)));
  });

  it("returns top-left corners, not centres", () => {
    const [placed] = layout([table("a")], []);
    expect(placed.x).toBeGreaterThanOrEqual(0);
    expect(placed.y).toBeGreaterThanOrEqual(0);
  });
});
