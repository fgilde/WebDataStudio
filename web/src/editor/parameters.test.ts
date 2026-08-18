import { describe, expect, it } from "vitest";
import { applyParameters, findParameters } from "./parameters";

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
