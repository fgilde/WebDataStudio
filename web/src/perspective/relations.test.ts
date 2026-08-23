import { describe, expect, it } from "vitest";
import { filterForValue, refOfTable, relationsOf } from "./relations";
import type { DiagramDto } from "../api";

const edge = (source: string, target: string, from: string, to: string, resolved = true) => ({
  name: `fk_${source}_${from}`,
  source, target, sourceColumns: [from], targetColumns: [to], resolved,
});

const diagram: DiagramDto = {
  nodes: [],
  edges: [
    edge("public.orders", "public.customers", "customer_id", "id"),
    edge("public.order_items", "public.orders", "order_id", "id"),
    // A table related twice over different columns: both steps are real.
    edge("public.orders", "public.addresses", "billing_address_id", "id"),
    edge("public.orders", "public.addresses", "shipping_address_id", "id"),
  ],
};

describe("relationsOf", () => {
  it("finds the step to the row a key points at, and the rows that point back", () => {
    const relations = relationsOf(diagram, "public.orders");

    const out = relations.filter(r => r.direction === "out");
    const inbound = relations.filter(r => r.direction === "in");

    expect(out.map(r => r.table)).toEqual([
      "public.customers", "public.addresses", "public.addresses",
    ]);
    expect(inbound).toHaveLength(1);
    expect(inbound[0]).toMatchObject({ table: "public.order_items", from: "id", to: "order_id" });
  });

  it("names the column, because the same table can be related twice", () => {
    const labels = relationsOf(diagram, "public.orders")
      .filter(r => r.table === "public.addresses")
      .map(r => r.label);

    expect(labels).toEqual(["addresses (billing_address_id)", "addresses (shipping_address_id)"]);
  });

  it("is case-insensitive about the table it was asked for", () => {
    expect(relationsOf(diagram, "PUBLIC.ORDERS")).toHaveLength(4);
  });

  it("leaves out an edge whose other end was filtered away", () => {
    const dangling: DiagramDto = {
      nodes: [], edges: [edge("public.orders", "other.customers", "customer_id", "id", false)],
    };

    expect(relationsOf(dangling, "public.orders")).toEqual([]);
  });

  it("leaves out a composite key: one value cannot follow two columns", () => {
    const composite: DiagramDto = {
      nodes: [],
      edges: [{
        name: "fk", source: "public.a", target: "public.b",
        sourceColumns: ["x", "y"], targetColumns: ["x", "y"], resolved: true,
      }],
    };

    expect(relationsOf(composite, "public.a")).toEqual([]);
  });
});

describe("refOfTable", () => {
  it("splits the schema off, and copes with a name that has none", () => {
    expect(refOfTable("public.orders")).toBe("Table:public/orders");
    expect(refOfTable("orders")).toBe("Table:orders");
  });
});

describe("filterForValue", () => {
  it("writes the filter in the language the server already parses", () => {
    expect(filterForValue(42)).toBe("=42");
    expect(filterForValue("ada@example.com")).toBe("=ada@example.com");
  });

  it("quotes anything with a space or a comma, so the parser keeps it in one piece", () => {
    expect(filterForValue("two words")).toBe('="two words"');
    expect(filterForValue("a,b")).toBe('="a,b"');
    expect(filterForValue('say "hi"')).toBe('="say ""hi"""');
  });

  it("asks for the rows with no value at all rather than for an empty one", () => {
    expect(filterForValue(null)).toBe("NULL");
  });
});
