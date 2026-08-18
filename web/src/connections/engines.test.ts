import { describe, expect, it } from "vitest";
import { engineFromConnectionString, ENGINES } from "./engines";

describe("engineFromConnectionString", () => {
  it("detects a postgres url", () =>
    expect(engineFromConnectionString("postgres://app:pw@db:5432/shop")).toBe("postgresql"));

  it("detects a postgres ado string", () =>
    expect(engineFromConnectionString("Host=db;Database=shop;Username=app")).toBe("postgresql"));

  it("detects a sql server ado string", () =>
    expect(engineFromConnectionString("Server=db,1433;Database=shop;User Id=sa")).toBe("sqlserver"));

  it("detects a mongodb url", () =>
    expect(engineFromConnectionString("mongodb://db:27017/shop")).toBe("mongodb"));

  it("returns null for unrecognised text", () =>
    expect(engineFromConnectionString("hello world")).toBeNull());
});

describe("ENGINES", () => {
  it("covers every engine the server accepts", () => {
    expect(ENGINES.map(e => e.id).sort()).toEqual(
      ["clickhouse", "duckdb", "mongodb", "mysql", "oracle", "postgresql", "redis", "sqlite", "sqlserver"]);
  });
});
