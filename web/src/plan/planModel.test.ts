import { describe, expect, it } from "vitest";
import type { PlanNodeDto } from "../api";
import {
  decodePlanBytes, encodePlanBytes,
  costShare, edgeWidth, filterProperties, flattenPlan, layoutPlan, operatorFamily, ownCost, rowsOut,
} from "./planModel";

const node = (operation: string, cost: number, children: PlanNodeDto[] = [],
  extra: Partial<PlanNodeDto> = {}): PlanNodeDto => ({
  operation, detail: null, estimatedCost: cost, estimatedRows: 10, actualRows: null, actualMs: null,
  children, warnings: [], ...extra,
});

describe("planModel", () => {
  it("groups operators into families", () => {
    expect(operatorFamily("Clustered Index Seek")).toBe("seek");
    expect(operatorFamily("Index Scan")).toBe("scan");
    expect(operatorFamily("Table Scan")).toBe("scan");
    expect(operatorFamily("Key Lookup")).toBe("lookup");
    expect(operatorFamily("Nested Loops")).toBe("join");
    expect(operatorFamily("Hash Match")).toBe("join");
    expect(operatorFamily("Stream Aggregate")).toBe("aggregate");
    expect(operatorFamily("Sort")).toBe("sort");
    expect(operatorFamily("Table Spool")).toBe("spool");
    expect(operatorFamily("Compute Scalar")).toBe("compute");
    expect(operatorFamily("Filter")).toBe("filter");
    expect(operatorFamily("Parallelism")).toBe("parallelism");
    expect(operatorFamily("Clustered Index Update")).toBe("write");
    expect(operatorFamily("Seq Scan")).toBe("scan");
    expect(operatorFamily("Something New")).toBe("other");
  });

  it("own cost is the subtree minus the children, and never negative", () => {
    const tree = node("Nested Loops", 10, [node("Index Seek", 3), node("Table Scan", 5)]);
    expect(ownCost(tree)).toBe(2);
    expect(ownCost(node("Sort", 1, [node("Scan", 2)]))).toBe(0);
    expect(costShare(tree, 10)).toBeCloseTo(0.2);
    expect(costShare(tree, null)).toBeNull();
  });

  it("rows out prefers the actual count", () => {
    expect(rowsOut(node("Scan", 1, [], { estimatedRows: 5, actualRows: 7 }))).toBe(7);
    expect(rowsOut(node("Scan", 1, [], { estimatedRows: 5 }))).toBe(5);
    expect(rowsOut(node("Scan", 1, [], { estimatedRows: null }))).toBeNull();
  });

  it("edges widen with the rows they carry", () => {
    expect(edgeWidth(null)).toBe(1);
    expect(edgeWidth(1)).toBeLessThan(edgeWidth(1000));
    expect(edgeWidth(1000)).toBeLessThan(edgeWidth(1_000_000));
    expect(edgeWidth(1e12)).toBeLessThanOrEqual(12);
  });

  it("flattens with stable ids and parents", () => {
    const rows = flattenPlan(node("Root", 1, [node("A", 0), node("B", 0, [node("C", 0)])]));
    expect(rows.map(r => [r.id, r.parentId])).toEqual([
      ["0", null], ["0.0", "0"], ["0.1", "0"], ["0.1.0", "0.1"],
    ]);
  });

  it("lays out right to left: the root is left of its children", () => {
    const { nodes, edges } = layoutPlan(node("Root", 1, [node("A", 0), node("B", 0)]));
    const x = (id: string) => nodes.find(n => n.id === id)!.position.x;
    expect(x("0")).toBeLessThan(x("0.0"));
    expect(x("0")).toBeLessThan(x("0.1"));
    // Data flows from the child into its parent.
    expect(edges.map(e => [e.source, e.target])).toEqual([["0.0", "0"], ["0.1", "0"]]);
  });

  it("filters properties by name or value and keeps the path to a match", () => {
    const props = [
      { name: "Estimate Rows", value: "10", children: [] },
      { name: "Index Scan", value: null, children: [{ name: "Object", value: "[dbo].[Orders]", children: [] }] },
    ];
    expect(filterProperties(props, "")).toEqual(props);
    expect(filterProperties(props, "orders")).toEqual([
      { name: "Index Scan", value: null, children: [{ name: "Object", value: "[dbo].[Orders]", children: [] }] },
    ]);
    expect(filterProperties(props, "estimate").map(p => p.name)).toEqual(["Estimate Rows"]);
  });

  it("reads a plan the way SSMS saves it: UTF-16 with a byte order mark", () => {
    const text = '<?xml version="1.0" encoding="utf-16"?><ShowPlanXML/>';
    const le = new Uint8Array([0xff, 0xfe, ...[...text].flatMap(c => [c.charCodeAt(0), 0])]);
    const be = new Uint8Array([0xfe, 0xff, ...[...text].flatMap(c => [0, c.charCodeAt(0)])]);
    const utf8bom = new Uint8Array([0xef, 0xbb, 0xbf, ...new TextEncoder().encode(text)]);
    expect(decodePlanBytes(le.buffer)).toBe(text);
    expect(decodePlanBytes(be.buffer)).toBe(text);
    expect(decodePlanBytes(utf8bom.buffer)).toBe(text);
    expect(decodePlanBytes(new TextEncoder().encode(text).buffer)).toBe(text);
  });

  it("writes a plan back as UTF-16 LE with a byte order mark, as SSMS expects", () => {
    const bytes = encodePlanBytes("<a/>");
    expect([...bytes]).toEqual([0xff, 0xfe, 0x3c, 0, 0x61, 0, 0x2f, 0, 0x3e, 0]);
    expect(decodePlanBytes(bytes.buffer as ArrayBuffer)).toBe("<a/>");
  });
});
