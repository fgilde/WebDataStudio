import { describe, expect, it } from "vitest";
import { fuzzyMatches, fuzzyRank, fuzzyScore } from "./fuzzy";

describe("fuzzyMatches", () => {
  it("matches a subsequence, the way go-to-file does", () => {
    expect(fuzzyMatches("ordit", "order_items")).toBe(true);
    expect(fuzzyMatches("abpu", "AbpUsers")).toBe(true);
  });

  it("does not match what is not there", () => {
    expect(fuzzyMatches("xyz", "order_items")).toBe(false);
    // Order matters: the characters have to appear in sequence.
    expect(fuzzyMatches("tirdo", "order_items")).toBe(false);
  });

  it("ignores case on both sides", () => {
    expect(fuzzyMatches("USERS", "AbpUsers")).toBe(true);
    expect(fuzzyMatches("abpusers", "ABPUSERS")).toBe(true);
  });

  it("matches everything for an empty needle", () => {
    expect(fuzzyMatches("", "anything")).toBe(true);
  });
});

describe("fuzzyScore", () => {
  it("prefers a prefix over a word boundary over a scattered match", () => {
    const prefix = fuzzyScore("order", "orders");
    const boundary = fuzzyScore("order", "shop_order_log");
    const scattered = fuzzyScore("order", "old_reserved");

    expect(prefix).toBeLessThan(boundary);
    expect(boundary).toBeLessThan(scattered);
  });

  it("prefers the exact name", () => {
    expect(fuzzyScore("orders", "orders")).toBeLessThan(fuzzyScore("orders", "orders_archive"));
  });

  it("prefers the shorter of two equally placed names", () => {
    expect(fuzzyScore("user", "users")).toBeLessThan(fuzzyScore("user", "users_with_a_long_name"));
  });

  it("is infinite for a name that does not match", () => {
    expect(fuzzyScore("xyz", "orders")).toBe(Number.POSITIVE_INFINITY);
  });
});

describe("fuzzyRank", () => {
  const tables = ["reordering_log", "orders", "order_items", "customers"]
    .map(name => ({ name }));

  it("puts the best match first and drops what does not match", () => {
    const ranked = fuzzyRank(tables, "order", t => t.name);

    expect(ranked[0].name).toBe("orders");
    expect(ranked.map(t => t.name)).not.toContain("customers");
  });

  it("respects the limit", () => {
    expect(fuzzyRank(tables, "o", t => t.name, 2)).toHaveLength(2);
  });
});
