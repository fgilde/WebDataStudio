import { describe, expect, it } from "vitest";
import { buildSelect, emptyModel, filterParameters, type QueryModel } from "./buildSelect";

const base = (): QueryModel => ({
  ...emptyModel(),
  tables: [{ name: "people", alias: "a" }],
  columns: [{ table: "a", column: "id" }, { table: "a", column: "name" }],
});

describe("buildSelect", () => {
  it("builds a plain select over one table", () => {
    expect(buildSelect(base(), "postgresql"))
      .toBe('SELECT "a"."id", "a"."name"\n  FROM "people" "a";');
  });

  it("returns nothing for an empty model rather than invalid SQL", () => {
    expect(buildSelect(emptyModel(), "postgresql")).toBe("");
    expect(buildSelect({ ...emptyModel(), tables: [{ name: "t", alias: "a" }] }, "postgresql")).toBe("");
  });

  it("writes the chosen join kind and condition", () => {
    const model: QueryModel = {
      ...base(),
      tables: [{ name: "people", alias: "a" }, { name: "orders", alias: "b" }],
      joins: [{ left: "a", right: "b", leftColumn: "id", rightColumn: "person_id", kind: "LEFT" }],
    };

    const sql = buildSelect(model, "postgresql");
    expect(sql).toContain('LEFT JOIN "orders" "b" ON "a"."id" = "b"."person_id"');
  });

  it("cross joins a table nobody joined, instead of dropping it", () => {
    const model: QueryModel = {
      ...base(),
      tables: [{ name: "people", alias: "a" }, { name: "config", alias: "b" }],
    };

    expect(buildSelect(model, "postgresql")).toContain('CROSS JOIN "config" "b"');
  });

  it("parameterises a filter instead of pasting the value", () => {
    const model: QueryModel = {
      ...base(),
      filters: [{ table: "a", column: "name", operator: "=", value: "'; DROP TABLE people; --" }],
    };

    const sql = buildSelect(model, "postgresql");
    expect(sql).toContain('WHERE "a"."name" = :p1');
    expect(sql).not.toContain("DROP TABLE");
    expect(filterParameters(model)).toEqual({ p1: "'; DROP TABLE people; --" });
  });

  it("writes a null check without a parameter", () => {
    const model: QueryModel = {
      ...base(),
      filters: [{ table: "a", column: "name", operator: "IS NULL", value: "" }],
    };

    expect(buildSelect(model, "postgresql")).toContain('WHERE "a"."name" IS NULL');
    expect(filterParameters(model)).toEqual({});
  });

  it("groups by the columns that are not aggregated", () => {
    const model: QueryModel = {
      ...base(),
      columns: [{ table: "a", column: "name" }, { table: "a", column: "id", aggregate: "count" }],
      grouping: true,
    };

    const sql = buildSelect(model, "postgresql");
    expect(sql).toContain('count("a"."id")');
    expect(sql).toContain('GROUP BY "a"."name"');
  });

  it("orders and pages in the dialect's spelling", () => {
    const model: QueryModel = { ...base(), order: [{ table: "a", column: "id", descending: true }], limit: 10 };

    expect(buildSelect(model, "postgresql")).toContain("LIMIT 10");
    expect(buildSelect(model, "sqlserver")).toContain("OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY");
    expect(buildSelect(model, "postgresql")).toContain('ORDER BY "a"."id" DESC');
  });

  it("quotes identifiers the way each engine does", () => {
    expect(buildSelect(base(), "mysql")).toContain("`people`");
    expect(buildSelect(base(), "sqlserver")).toContain("[people]");
    expect(buildSelect(base(), "postgresql")).toContain('"people"');
  });

  it("qualifies a table with its schema", () => {
    const model: QueryModel = { ...base(), tables: [{ name: "people", schema: "public", alias: "a" }] };
    expect(buildSelect(model, "postgresql")).toContain('"public"."people" "a"');
  });
});
