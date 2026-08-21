import { describe, expect, it } from "vitest";
import { fromMarkdown, toMarkdown, type Cell } from "./notebook";

const cells = (...items: Omit<Cell, "id">[]): Cell[] =>
  items.map((item, index) => ({ id: `c${index}`, ...item }));

describe("notebook", () => {
  it("keeps order, kind and connection through a round trip", () => {
    const original = cells(
      { kind: "note", text: "Why the report was wrong:" },
      { kind: "sql", text: "SELECT count(*) FROM orders", connectionId: "env-abc" },
      { kind: "note", text: "One order had no customer." },
      { kind: "sql", text: "SELECT * FROM orders\n WHERE customer_id IS NULL", connectionId: "env-def" },
    );

    const back = fromMarkdown(toMarkdown(original));

    expect(back.map(c => [c.kind, c.text, c.connectionId]))
      .toEqual(original.map(c => [c.kind, c.text, c.connectionId]));
  });

  it("reads a fenced sql block as a sql cell", () => {
    const parsed = fromMarkdown("```sql\nSELECT 1\n```\n");

    expect(parsed).toHaveLength(1);
    expect(parsed[0].kind).toBe("sql");
    expect(parsed[0].text).toBe("SELECT 1");
    expect(parsed[0].connectionId).toBeUndefined();
  });

  it("reads prose as a note", () => {
    const parsed = fromMarkdown("Some context.\nOn two lines.\n");

    expect(parsed).toHaveLength(1);
    expect(parsed[0].kind).toBe("note");
    expect(parsed[0].text).toBe("Some context.\nOn two lines.");
  });

  it("carries the connection in the info string", () => {
    expect(toMarkdown(cells({ kind: "sql", text: "SELECT 1", connectionId: "env-1" })))
      .toContain("```sql conn=env-1");
  });

  it("splits prose around a block into separate notes", () => {
    const parsed = fromMarkdown("before\n\n```sql\nSELECT 1\n```\n\nafter\n");

    expect(parsed.map(c => c.kind)).toEqual(["note", "sql", "note"]);
    expect(parsed[0].text).toBe("before");
    expect(parsed[2].text).toBe("after");
  });

  /// A document somebody was in the middle of writing must not lose its last cell.
  it("keeps an unclosed block", () => {
    const parsed = fromMarkdown("```sql conn=env-9\nSELECT 1");

    expect(parsed).toHaveLength(1);
    expect(parsed[0].kind).toBe("sql");
    expect(parsed[0].connectionId).toBe("env-9");
  });

  it("drops empty cells on the way out", () => {
    const markdown = toMarkdown(cells(
      { kind: "note", text: "   " },
      { kind: "sql", text: "SELECT 1" },
    ));

    expect(markdown.trim()).toBe("```sql\nSELECT 1\n```");
  });

  // A fence that is not SQL is prose as far as the notebook is concerned; it stays in the note it
  // came with rather than becoming a cell that cannot be run.
  it("leaves a non-sql fence in the prose", () => {
    const parsed = fromMarkdown("```json\n{\"a\": 1}\n```\n");

    expect(parsed).toHaveLength(1);
    expect(parsed[0].kind).toBe("note");
    expect(parsed[0].text).toContain("```json");
  });
});
