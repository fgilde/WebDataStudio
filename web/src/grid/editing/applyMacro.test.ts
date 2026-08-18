import { describe, expect, it } from "vitest";
import { applyMacro, macroError } from "./applyMacro";

describe("applyMacro", () => {
  it("sets a fixed value", () => expect(applyMacro("x", { kind: "set", value: "y" })).toBe("y"));
  it("clears to null", () => expect(applyMacro("x", { kind: "null" })).toBeNull());
  it("trims", () => expect(applyMacro("  x  ", { kind: "trim" })).toBe("x"));
  it("uppercases", () => expect(applyMacro("ab", { kind: "upper" })).toBe("AB"));
  it("lowercases", () => expect(applyMacro("AB", { kind: "lower" })).toBe("ab"));

  it("replaces literally", () =>
    expect(applyMacro("a.b", { kind: "replace", find: ".", with: "-", regex: false })).toBe("a-b"));

  it("replaces by regex", () =>
    expect(applyMacro("a1b2", { kind: "replace", find: "[0-9]", with: "", regex: true })).toBe("ab"));

  it("leaves the value alone when the regex is invalid", () =>
    expect(applyMacro("a", { kind: "replace", find: "(", with: "", regex: true })).toBe("a"));

  it("adds to a number", () => expect(applyMacro(5, { kind: "add", amount: 2 })).toBe(7));

  it("leaves a non-numeric value alone instead of producing NaN", () =>
    expect(applyMacro("abc", { kind: "add", amount: 2 })).toBe("abc"));

  it("substitutes value and row in a template", () =>
    expect(applyMacro("x", { kind: "template", pattern: "{value}-{row}" }, 4)).toBe("x-5"));
});

describe("macroError", () => {
  it("reports an invalid regex", () =>
    expect(macroError({ kind: "replace", find: "(", with: "", regex: true })).not.toBeNull());

  it("is null for a valid macro", () => expect(macroError({ kind: "trim" })).toBeNull());
});
