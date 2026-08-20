import { describe, expect, it } from "vitest";
import { suggestJoin, type LoadedTable } from "./buildSelect";

const people: LoadedTable = {
  alias: "a", name: "people", schema: "main", columns: ["id", "name"], foreignKeys: [],
};

const orders: LoadedTable = {
  alias: "b", name: "orders", schema: "main", columns: ["id", "person_id", "total"],
  foreignKeys: [{
    name: "fk_orders_people", columns: ["person_id"],
    referencedSchema: "main", referencedTable: "people", referencedColumns: ["id"],
    onDelete: "NO ACTION", onUpdate: "NO ACTION",
  }],
};

const unrelated: LoadedTable = {
  alias: "c", name: "settings", schema: "main", columns: ["key", "value"], foreignKeys: [],
};

describe("suggestJoin", () => {
  it("joins the child to the parent it references", () => {
    expect(suggestJoin(people, orders)).toEqual({
      left: "a", leftColumn: "id", right: "b", rightColumn: "person_id", kind: "INNER",
    });
  });

  // The order tables are added in is the user's business, not the foreign key's.
  it("finds the key when the parent is added second", () => {
    expect(suggestJoin(orders, people)).toEqual({
      left: "b", leftColumn: "person_id", right: "a", rightColumn: "id", kind: "INNER",
    });
  });

  it("has nothing to say about two unrelated tables", () => {
    expect(suggestJoin(people, unrelated)).toBeNull();
  });

  it("pairs composite keys column by column", () => {
    const parent: LoadedTable = {
      alias: "p", name: "region", schema: "main", columns: ["country", "code"], foreignKeys: [],
    };
    const child: LoadedTable = {
      alias: "q", name: "city", schema: "main", columns: ["country", "region_code"],
      foreignKeys: [{
        name: "fk", columns: ["country", "region_code"],
        referencedSchema: "main", referencedTable: "region", referencedColumns: ["country", "code"],
        onDelete: "NO ACTION", onUpdate: "NO ACTION",
      }],
    };

    // The first column pair carries the join; the rest is offered as an extra condition.
    expect(suggestJoin(parent, child)).toEqual({
      left: "p", leftColumn: "country", right: "q", rightColumn: "country", kind: "INNER",
      extra: [{ leftColumn: "code", rightColumn: "region_code" }],
    });
  });

  // A schema is only a tie-breaker: engines that have none must still match.
  it("matches on the table name when neither side names a schema", () => {
    const withoutSchema: LoadedTable = { ...orders, schema: undefined };
    expect(suggestJoin({ ...people, schema: undefined }, withoutSchema)).not.toBeNull();
  });

  it("does not match a same-named table in another schema", () => {
    expect(suggestJoin({ ...people, schema: "other" }, orders)).toBeNull();
  });
});
