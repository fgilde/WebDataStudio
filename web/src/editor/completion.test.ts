import { describe, expect, it } from "vitest";
import { collectAliases, completionContext } from "./completion";

describe("collectAliases", () => {
  it("finds an explicit alias", () =>
    expect(collectAliases("SELECT * FROM users u").get("u")).toBe("users"));

  it("finds an AS alias", () =>
    expect(collectAliases("SELECT * FROM users AS u").get("u")).toBe("users"));

  it("finds join aliases too", () => {
    const aliases = collectAliases("SELECT * FROM users u JOIN orders o ON o.user_id = u.id");
    expect(aliases.get("o")).toBe("orders");
  });

  it("maps a schema-qualified table to its bare name", () =>
    expect(collectAliases("SELECT * FROM public.users u").get("u")).toBe("users"));

  it("maps a table to itself so an unaliased dot completes", () =>
    expect(collectAliases("SELECT * FROM users").get("users")).toBe("users"));

  it("does not mistake a keyword for an alias", () =>
    expect(collectAliases("SELECT * FROM users WHERE id = 1").get("where")).toBeUndefined());
});

describe("completionContext", () => {
  it("asks for columns of the aliased table after a dot", () =>
    expect(completionContext("SELECT u. FROM users u", 9)).toEqual({ kind: "columns", table: "users" }));

  it("asks for tables right after FROM", () =>
    expect(completionContext("SELECT * FROM ", 14).kind).toBe("tables"));

  it("asks for tables after JOIN", () =>
    expect(completionContext("SELECT * FROM a JOIN ", 21).kind).toBe("tables"));

  it("falls back to everything elsewhere", () =>
    expect(completionContext("SELECT ", 7).kind).toBe("any"));
});
