import { describe, expect, it } from "vitest";
import {
  buildSelect, buildSelectWithModel, emptyModel, filterParameters, parseModel, type QueryModel,
} from "./buildSelect";
import { askedFor } from "../editor/parameters";

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

  it("numbers a parameter by its filter, even after a null check", () => {
    const model: QueryModel = {
      ...base(),
      filters: [
        { table: "a", column: "name", operator: "IS NULL", value: "" },
        { table: "a", column: "id", operator: "=", value: "7" },
      ],
    };

    expect(buildSelect(model, "postgresql")).toContain('"a"."id" = :p2');
    expect(filterParameters(model)).toEqual({ p2: "7" });
  });

  it("combines filters with the connector each one names", () => {
    const sql = buildSelect({
      ...base(),
      filters: [
        { table: "a", column: "name", operator: "=", value: "x", connector: "OR" },
        { table: "a", column: "name", operator: "=", value: "y", connector: "OR" },
        { table: "a", column: "id", operator: ">", value: "1" },
      ],
    }, "postgresql");

    expect(sql).toContain(' WHERE "a"."name" = :p1\n    OR "a"."name" = :p2\n   AND "a"."id" > :p3');
  });

  it("keeps an OR from swallowing the EXISTS after it", () => {
    const sql = buildSelect({
      ...base(),
      filters: [
        { table: "a", column: "name", operator: "=", value: "x" },
        { table: "a", column: "name", operator: "=", value: "y", connector: "OR" },
      ],
      exists: [{ name: "orders", outerTable: "a", outerColumn: "id", column: "person_id", negated: true }],
    }, "postgresql");

    expect(sql).toContain(' WHERE ("a"."name" = :p1\n    OR "a"."name" = :p2)\n   AND NOT EXISTS');
  });

  // The joins come in the order they were drawn; the FROM needs each ON to name placed tables only.
  it("orders the joins so every condition names a table already joined", () => {
    const sql = buildSelect({
      ...base(),
      tables: [{ name: "people", alias: "a" }, { name: "orders", alias: "b" }, { name: "lines", alias: "c" }],
      joins: [
        { left: "b", right: "c", leftColumn: "id", rightColumn: "order_id", kind: "INNER" },
        { left: "a", right: "b", leftColumn: "id", rightColumn: "person_id", kind: "INNER" },
      ],
    }, "postgresql");

    expect(sql).toContain('FROM "people" "a"\n  INNER JOIN "orders" "b" ON "a"."id" = "b"."person_id"\n'
      + '  INNER JOIN "lines" "c" ON "b"."id" = "c"."order_id"');
  });

  it("turns a join drawn towards the first table around", () => {
    const sql = buildSelect({
      ...base(),
      tables: [{ name: "people", alias: "a" }, { name: "orders", alias: "b" }],
      joins: [{ left: "b", right: "a", leftColumn: "person_id", rightColumn: "id", kind: "LEFT" }],
    }, "postgresql");

    expect(sql).toContain('RIGHT JOIN "orders" "b" ON "b"."person_id" = "a"."id"');
    expect(sql).not.toContain('JOIN "people"');
  });

  it("adds a second join between placed tables to the ON, not the table twice", () => {
    const sql = buildSelect({
      ...base(),
      tables: [{ name: "people", alias: "a" }, { name: "orders", alias: "b" }],
      joins: [
        { left: "a", right: "b", leftColumn: "id", rightColumn: "person_id", kind: "INNER" },
        { left: "a", right: "b", leftColumn: "tenant", rightColumn: "tenant", kind: "INNER" },
      ],
    }, "postgresql");

    expect(sql).toContain('ON "a"."id" = "b"."person_id" AND "a"."tenant" = "b"."tenant"');
    expect(sql.match(/"orders"/g)).toHaveLength(1);
  });

  it("cross joins an unconnected first table before the joins that follow", () => {
    const sql = buildSelect({
      ...base(),
      tables: [{ name: "config", alias: "a" }, { name: "orders", alias: "b" }, { name: "lines", alias: "c" }],
      joins: [{ left: "c", right: "b", leftColumn: "order_id", rightColumn: "id", kind: "INNER" }],
    }, "postgresql");

    expect(sql).toContain('CROSS JOIN "orders" "b"\n  INNER JOIN "lines" "c" ON "c"."order_id" = "b"."id"');
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
    expect(sql).toContain(`WHERE "a"."total" > :p1`);
    expect(parseModel(sql)!.filters[0].value).toBe("");
  });

  it("keeps an alias that would close the comment from closing it", () => {
    const sql = buildSelectWithModel({
      ...base(),
      columns: [{ table: "a", column: "total", alias: "x */ y" }],
    }, "postgresql");

    expect(parseModel(sql)!.columns[0].alias).toBe("x */ y");
  });

  it("hands the filter values over beside the SQL, named like its parameters", () => {
    expect(filterParameters({
      ...base(),
      columns: [{ table: "a", column: "total" }],
      filters: [
        { table: "a", column: "note", operator: "IS NULL", value: "" },
        { table: "a", column: "id", operator: "IN", value: "1, 2" },
      ],
    })).toEqual({ p2: "1, 2" });
  });

  // The query tab asks for what its own scanner finds; a marker it does not read would run the
  // statement unbound instead of offering the builder's values.
  it.each(["postgresql", "sqlserver", "mysql", "sqlite", "duckdb", "oracle"] as const)(
    "names its parameters the way the %s query tab asks for them", dialect => {
      const model: QueryModel = {
        ...base(),
        columns: [{ table: "a", column: "total" }],
        filters: [
          { table: "a", column: "total", operator: ">", value: "50" },
          { table: "a", column: "id", operator: "IS NULL", value: "" },
          { table: "a", column: "note", operator: "=", value: "x" },
        ],
      };

      expect(askedFor(buildSelectWithModel(model, dialect), dialect))
        .toEqual(Object.keys(filterParameters(model)));
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
