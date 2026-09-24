import { describe, expect, it } from "vitest";
import {
  applyParameters, askedFor, declaredValues, findDeclarations, findParameters, prepareRun,
} from "./parameters";

describe("findParameters", () => {
  it("finds a colon parameter for PostgreSQL and Oracle", () => {
    expect(findParameters("SELECT * FROM t WHERE id = :id", "postgresql")).toEqual(["id"]);
    expect(findParameters("SELECT * FROM t WHERE id = :id", "oracle")).toEqual(["id"]);
  });

  it("finds an at parameter for SQL Server and MySQL", () => {
    expect(findParameters("SELECT * FROM t WHERE id = @id", "sqlserver")).toEqual(["id"]);
    expect(findParameters("SELECT * FROM t WHERE id = @id", "mysql")).toEqual(["id"]);
  });

  it("finds a dollar parameter for SQLite", () => {
    expect(findParameters("SELECT * FROM t WHERE id = $id", "sqlite")).toEqual(["id"]);
  });

  it("ignores a PostgreSQL cast", () => {
    expect(findParameters("SELECT id::text FROM t", "postgresql")).toEqual([]);
    expect(findParameters("SELECT id::text FROM t WHERE id = :wanted", "postgresql")).toEqual(["wanted"]);
  });

  it("ignores a server variable", () => {
    expect(findParameters("SELECT @@version", "sqlserver")).toEqual([]);
  });

  it("ignores a marker inside a string literal or a comment", () => {
    expect(findParameters("SELECT ':notaparam' FROM t", "postgresql")).toEqual([]);
    expect(findParameters("-- :nope\nSELECT 1", "postgresql")).toEqual([]);
    expect(findParameters("/* :nope */ SELECT 1", "postgresql")).toEqual([]);
  });

  it("deduplicates but keeps first-appearance order", () => {
    expect(findParameters("SELECT * FROM t WHERE a = :b AND c = :a AND d = :b", "postgresql"))
      .toEqual(["b", "a"]);
  });

  it("finds nothing in an empty statement", () => {
    expect(findParameters("", "postgresql")).toEqual([]);
    expect(findParameters("   ", "postgresql")).toEqual([]);
  });

  it("finds nothing for an engine without bind variables", () => {
    expect(findParameters("db.people.find({})", "mongodb")).toEqual([]);
  });

  // The dialog shows the script's own variables too, prefilled, so they can be overridden.
  it("lists declared variables as well, in order of appearance", () => {
    expect(findParameters("DECLARE @a int = 1;\nSELECT @a, @b", "sqlserver")).toEqual(["a", "b"]);
  });
});

const SCRIPT = [
  "DECLARE @CutoffDate datetimeoffset = '2026-09-22 23:59:59 +00:00'",
  "DECLARE @seedAll bit = 0;",
  "SELECT S.Id FROM dbo.Shipments S",
  "WHERE (@seedAll = 1 OR S.DateUpdatedUtc >= @CutoffDate) AND S.Owner = @owner",
].join("\n");

describe("declaredValues", () => {
  it("reads what the script declares, as a person would type it", () => {
    expect(declaredValues(SCRIPT, "sqlserver"))
      .toEqual({ CutoffDate: "2026-09-22 23:59:59 +00:00", seedAll: "0" });
  });

  it("knows every variable of a list, and neither the defaults' variables nor a table's columns", () => {
    const sql = "DECLARE @a int = COALESCE(@given, 1), @b nvarchar(10), @t TABLE (x int, y int), @s nvarchar(9) = N'it''s';\n"
      + "SELECT @a, @b, @given FROM @t";

    expect(declaredValues(sql, "sqlserver")).toEqual({ a: "COALESCE(@given, 1)", s: "it's" });
    expect(findDeclarations(sql, "sqlserver").map(d => d.name)).toEqual(["a", "b", "t", "s"]);
  });

  it("ends a declaration without a semicolon at the next statement", () => {
    expect(declaredValues("DECLARE @a int = 1\nSELECT @a, 2", "sqlserver")).toEqual({ a: "1" });
  });

  it("is not fooled by a DECLARE in a comment or a string", () => {
    expect(findDeclarations("-- DECLARE @x int = 1\nSELECT 'DECLARE @y int = 2'", "sqlserver")).toEqual([]);
  });

  it("declares nothing on engines without DECLARE", () => {
    expect(findDeclarations("SELECT :a", "postgresql")).toEqual([]);
  });
});

describe("prepareRun", () => {
  // Sending a parameter named like a declared variable makes SQL Server refuse the DECLARE.
  it("sends only what the script does not declare", () => {
    const { sql, parameters } = prepareRun(SCRIPT,
      { CutoffDate: "2026-09-22 23:59:59 +00:00", seedAll: "0", owner: "ada" }, "sqlserver");

    expect(sql).toBe(SCRIPT);
    expect(parameters).toEqual({ owner: "ada" });
  });

  it("writes a changed value into its DECLARE, quoted like the original", () => {
    const { sql } = prepareRun(SCRIPT, { CutoffDate: "2026-01-01 it's", seedAll: "1" }, "sqlserver");

    expect(sql).toContain("DECLARE @CutoffDate datetimeoffset = '2026-01-01 it''s'\n");
    expect(sql).toContain("DECLARE @seedAll bit = 1;");
  });

  it("gives a declaration without a value the one from the dialog", () => {
    expect(prepareRun("DECLARE @n nvarchar(20);\nSELECT @n", { n: "ada" }, "sqlserver").sql)
      .toBe("DECLARE @n nvarchar(20) = 'ada';\nSELECT @n");
  });

  it("takes NULL as NULL and a number as a number", () => {
    expect(prepareRun("DECLARE @a int = 1, @b nvarchar(5) = 'x';", { a: "42", b: "null" }, "sqlserver").sql)
      .toBe("DECLARE @a int = 42, @b nvarchar(5) = NULL;");
  });

  it("runs a part without its DECLARE with the values as parameters", () => {
    const part = "SELECT S.Id FROM dbo.Shipments S WHERE @seedAll = 1";
    expect(prepareRun(part, { seedAll: "0" }, "sqlserver")).toEqual({ sql: part, parameters: { seedAll: "0" } });
  });
});

describe("applyParameters", () => {
  it("leaves the statement alone and collects the values", () => {
    const result = applyParameters("SELECT * FROM t WHERE id = :id", { id: "7" }, "postgresql");
    expect(result.sql).toBe("SELECT * FROM t WHERE id = :id");
    expect(result.parameters).toEqual({ id: "7" });
  });

  it("passes a missing value as null rather than dropping the parameter", () => {
    expect(applyParameters("SELECT :a, :b", { a: "1" }, "postgresql").parameters)
      .toEqual({ a: "1", b: null });
  });
});

describe("askedFor", () => {
  // What the dialog asks: a script that declares its variables runs as written, without one.
  it("asks for nothing when the text declares every variable it uses", () => {
    expect(askedFor(SCRIPT.replace(" AND S.Owner = @owner", ""), "sqlserver")).toEqual([]);
  });

  it("asks only for what the text does not declare", () => {
    expect(askedFor(SCRIPT, "sqlserver")).toEqual(["owner"]);
  });

  it("asks for the variables of a part run without its DECLARE", () => {
    expect(askedFor("SELECT 1 WHERE @seedAll = 1 AND @CutoffDate < SYSDATETIMEOFFSET()", "sqlserver"))
      .toEqual(["seedAll", "CutoffDate"]);
  });
});
