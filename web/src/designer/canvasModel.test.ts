import { describe, expect, it } from "vitest";
import { applyConnection, toGraph } from "./canvasModel";
import { emptyModel, type LoadedTable, type QueryModel } from "./buildSelect";

const people: LoadedTable = {
  alias: "a", name: "people", schema: "main", columns: ["id", "name"], foreignKeys: [],
};

const orders: LoadedTable = {
  alias: "b", name: "orders", schema: "main", columns: ["id", "person_id"],
  foreignKeys: [{
    name: "fk", columns: ["person_id"], referencedSchema: "main", referencedTable: "people",
    referencedColumns: ["id"], onDelete: "NO ACTION", onUpdate: "NO ACTION",
  }],
};

const model = (patch: Partial<QueryModel> = {}): QueryModel => ({
  ...emptyModel(),
  tables: [
    { name: "people", schema: "main", alias: "a" },
    { name: "orders", schema: "main", alias: "b" },
  ],
  ...patch,
});

describe("toGraph", () => {
  it("makes one node per table, keyed by its alias", () => {
    const { nodes } = toGraph(model(), [people, orders]);

    expect(nodes.map(n => n.id)).toEqual(["a", "b"]);
    expect(nodes[0].data.columns).toEqual(["id", "name"]);
    expect(nodes[0].data.label).toBe("people");
  });

  it("makes one edge per join, labelled with its kind", () => {
    const { edges } = toGraph(
      model({ joins: [{ left: "a", leftColumn: "id", right: "b", rightColumn: "person_id", kind: "LEFT" }] }),
      [people, orders]);

    const edge = edges[0];
    expect(edge.source).toBe("a");
    expect(edge.target).toBe("b");
    expect(edge.label).toBe("LEFT");
  });

  // A table dropped on the canvas before its columns arrive must not break the layout.
  it("survives a table it knows nothing about yet", () => {
    const { nodes } = toGraph(model(), [people]);
    expect(nodes).toHaveLength(2);
    expect(nodes[1].data.columns).toEqual([]);
  });

  it("places nodes at distinct positions", () => {
    const { nodes } = toGraph(model(), [people, orders]);
    expect(nodes[0].position).not.toEqual(nodes[1].position);
  });
});

describe("applyConnection", () => {
  it("uses the foreign key when the dragged pair has one", () => {
    const next = applyConnection(model(), [people, orders], { source: "a", target: "b" });

    expect(next.joins).toEqual([
      { left: "a", leftColumn: "id", right: "b", rightColumn: "person_id", kind: "INNER" },
    ]);
  });

  it("still joins two unrelated tables, with the columns left to be picked", () => {
    const settings: LoadedTable = {
      alias: "c", name: "settings", schema: "main", columns: ["key"], foreignKeys: [],
    };
    const next = applyConnection(
      { ...model(), tables: [...model().tables, { name: "settings", schema: "main", alias: "c" }] },
      [people, settings], { source: "a", target: "c" });

    expect(next.joins[0]).toEqual({
      left: "a", leftColumn: "id", right: "c", rightColumn: "key", kind: "INNER",
    });
  });

  it("does not add the same join twice", () => {
    const once = applyConnection(model(), [people, orders], { source: "a", target: "b" });
    const twice = applyConnection(once, [people, orders], { source: "a", target: "b" });

    expect(twice.joins).toHaveLength(1);
  });

  it("ignores a connection to itself", () => {
    const next = applyConnection(model(), [people, orders], { source: "a", target: "a" });
    expect(next.joins).toHaveLength(0);
  });
});
