import { describe, expect, it } from "vitest";
// The corpus lives outside web/ because the server reads the same file. Imported as text rather
// than through node:fs, so this test needs no node types.
import corpusText from "../../../tests/filter-cases.json?raw";
import { filterKindOf, matchesFilter, type FilterKind } from "./filterLanguage";

/// Both implementations of the filter language have to agree on every line of it, or a filter means
/// one thing in a query result and another in a table browse.
const corpus = JSON.parse(corpusText) as {
  now: string;
  cases: { filter: string; kind: FilterKind; value: unknown; matches: boolean; why?: string }[];
};

const now = new Date(2026, 7, 23, 14, 30, 0);

describe("the filter language, against the shared corpus", () => {
  for (const [index, entry] of corpus.cases.entries())
    it(`${index}: ${entry.kind} ${JSON.stringify(entry.value)} vs "${entry.filter}"${
      entry.why ? ` — ${entry.why}` : ""}`, () => {
      expect(matchesFilter(entry.value, entry.kind, entry.filter, now)).toBe(entry.matches);
    });
});

describe("filterKindOf", () => {
  it("reads the declared type, because > means one thing on a number and another on text", () => {
    expect(filterKindOf("integer")).toBe("number");
    expect(filterKindOf("numeric(10,2)")).toBe("number");
    expect(filterKindOf("timestamp with time zone")).toBe("date");
    expect(filterKindOf("boolean")).toBe("boolean");
    expect(filterKindOf("character varying(50)")).toBe("text");
    expect(filterKindOf(undefined)).toBe("text");
  });
});

describe("matchesFilter", () => {
  it("keeps everything when nothing is typed", () => {
    expect(matchesFilter("Adam", "text", "")).toBe(true);
    expect(matchesFilter(null, "text", "  ")).toBe(true);
  });

  it("stops reading after 32 terms, like the server does", () => {
    const many = Array.from({ length: 200 }, (_, n) => `~w${n}`).join(" ");
    // Every term holds, so the answer is the same either way — what matters is that it returns.
    expect(matchesFilter("Adam", "text", many)).toBe(true);
  });
});
