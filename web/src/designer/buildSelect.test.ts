import { describe, expect, it } from "vitest";
import {
  buildSelect, buildSelectWithModel, emptyModel, filterParameters, parseModel, type QueryModel,
} from "./buildSelect";

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
    expect(sql).toContain('COUNT("a"."id")');
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

describe("aggregates, HAVING and DISTINCT", () => {
  const base = (): QueryModel => ({
    ...emptyModel(),
    tables: [{ name: "orders", schema: "main", alias: "a" }],
  });

  it("groups by every plain column as soon as one column aggregates", () => {
    const sql = buildSelect({
      ...base(),
      columns: [
        { table: "a", column: "person_id" },
        { table: "a", column: "total", aggregate: "sum", alias: "spent" },
      ],
    }, "postgresql");

    expect(sql).toContain('SUM("a"."total") AS "spent"');
    expect(sql).toContain('GROUP BY "a"."person_id"');
  });

  it("renders HAVING after GROUP BY", () => {
    const sql = buildSelect({
      ...base(),
      columns: [
        { table: "a", column: "person_id" },
        { table: "a", column: "total", aggregate: "sum" },
      ],
      having: [{ table: "a", column: "total", operator: ">", value: "100", aggregate: "sum" }],
    }, "postgresql");

    expect(sql.indexOf("HAVING")).toBeGreaterThan(sql.indexOf("GROUP BY"));
    expect(sql).toContain('HAVING SUM("a"."total") > :h1');
  });

  it("renders DISTINCT", () => {
    const sql = buildSelect({
      ...base(), distinct: true, columns: [{ table: "a", column: "person_id" }],
    }, "postgresql");

    expect(sql).toContain("SELECT DISTINCT");
  });

  // The model rides along in a comment, which is what lets a generated query be reopened in the
  // builder without anybody writing a SQL parser.
  it("carries its model in a comment and reads it back", () => {
    const model: QueryModel = {
      ...base(), columns: [{ table: "a", column: "total", aggregate: "avg" }], distinct: false,
    };
    const sql = buildSelectWithModel(model, "postgresql");

    expect(sql).toContain("/* wds:model");
    expect(parseModel(sql)).toEqual(model);
  });

  // The comment travels with the SQL, so it must not carry what the user typed into a filter.
  it("keeps filter values out of the comment", () => {
    const sql = buildSelectWithModel({
      ...base(),
      columns: [{ table: "a", column: "total" }],
      filters: [{ table: "a", column: "total", operator: ">", value: "secret-value" }],
    }, "postgresql");

    expect(sql).not.toContain("secret-value");
    expect(parseModel(sql)!.filters[0].value).toBe("");
  });

  it("adds no comment to a query the builder cannot produce", () => {
    expect(buildSelectWithModel(emptyModel(), "postgresql")).toBe("");
  });

  it("has no model to read out of hand-written SQL", () => {
    expect(parseModel("SELECT 1")).toBeNull();
    expect(parseModel("SELECT 1 /* wds:model not-json */")).toBeNull();
  });
});

describe("EXISTS", () => {
  const customers = (): QueryModel => ({
    ...emptyModel(),
    tables: [{ name: "customers", alias: "a" }],
    columns: [{ table: "a", column: "id" }],
  });

  it("writes the condition a join cannot express", () => {
    const sql = buildSelect({
      ...customers(),
      exists: [{
        name: "orders", schema: "public", outerTable: "a", outerColumn: "id",
        column: "customer_id", negated: true,
      }],
    }, "postgresql");

    expect(sql).toContain('NOT EXISTS (SELECT 1 FROM "public"."orders" "x1"');
    expect(sql).toContain('WHERE "x1"."customer_id" = "a"."id")');
  });

  it("opens the WHERE clause when there is nothing else in it", () => {
    const sql = buildSelect({
      ...customers(),
      exists: [{ name: "orders", outerTable: "a", outerColumn: "id", column: "customer_id", negated: false }],
    }, "postgresql");

    expect(sql).toContain(' WHERE EXISTS');
    expect(sql).not.toContain("AND EXISTS");
  });

  it("joins onto the conditions that are already there", () => {
    const sql = buildSelect({
      ...customers(),
      filters: [{ table: "a", column: "country", operator: "=", value: "PT" }],
      exists: [{ name: "orders", outerTable: "a", outerColumn: "id", column: "customer_id", negated: true }],
    }, "postgresql");

    expect(sql).toContain('WHERE "a"."country" = :p1');
    expect(sql).toContain("   AND NOT EXISTS");
  });

  it("gives every subquery its own alias, so two cannot collide", () => {
    const sql = buildSelect({
      ...customers(),
      exists: [
        { name: "orders", outerTable: "a", outerColumn: "id", column: "customer_id", negated: true },
        { name: "carts", outerTable: "a", outerColumn: "id", column: "customer_id", negated: false },
      ],
    }, "postgresql");

    expect(sql).toContain('"x1"');
    expect(sql).toContain('"x2"');
  });

  it("skips an entry that is not finished being filled in", () => {
    const sql = buildSelect({
      ...customers(),
      exists: [{ name: "orders", outerTable: "a", outerColumn: "id", column: "", negated: true }],
    }, "postgresql");

    expect(sql).not.toContain("EXISTS");
  });

  it("quotes the way the engine does", () => {
    const model: QueryModel = {
      ...customers(),
      exists: [{ name: "orders", outerTable: "a", outerColumn: "id", column: "customer_id", negated: true }],
    };

    expect(buildSelect(model, "mysql")).toContain("`orders` `x1`");
    expect(buildSelect(model, "sqlserver")).toContain("[orders] [x1]");
  });

  it("survives the round trip through the model comment", () => {
    const sql = buildSelectWithModel({
      ...customers(),
      exists: [{ name: "orders", outerTable: "a", outerColumn: "id", column: "customer_id", negated: true }],
    }, "postgresql");

    expect(parseModel(sql)!.exists?.[0]).toMatchObject({ name: "orders", negated: true });
  });
});
