// @vitest-environment node
import { describe, expect, it } from "vitest";
import { parsePastedRows } from "./pasteRows";
import type { QueryColumn } from "../query/runQuery";

const columns = (...names: string[]): QueryColumn[] =>
  names.map(name => ({ name, dataType: "text", nullable: true }));

const people = columns("id", "name", "city");

describe("parsePastedRows", () => {
  it("reads what a spreadsheet copies: tab separated, no header", () => {
    const parsed = parsePastedRows("1\tada\tLondon\n2\tgrace\tNew York", people);

    expect(parsed.usedHeader).toBe(false);
    expect(parsed.rows).toEqual([
      { id: "1", name: "ada", city: "London" },
      { id: "2", name: "grace", city: "New York" },
    ]);
  });

  it("uses the first line as a header when every cell of it names a column", () => {
    const parsed = parsePastedRows("name,id\nada,1", people);

    expect(parsed.usedHeader).toBe(true);
    // By name, not by position: the paste had them the other way round.
    expect(parsed.rows).toEqual([{ name: "ada", id: "1" }]);
  });

  it("does not mistake data for a header", () => {
    const parsed = parsePastedRows("1,ada,London", people);

    expect(parsed.usedHeader).toBe(false);
    expect(parsed.rows).toEqual([{ id: "1", name: "ada", city: "London" }]);
  });

  it("names the columns this table does not have rather than swallowing them", () => {
    const parsed = parsePastedRows("id,name,nonsense\n1,ada,x", people);

    expect(parsed.usedHeader).toBe(false);
    expect(parsed.ignored).toEqual([]);
  });

  it("keeps a quoted comma in one cell", () => {
    const parsed = parsePastedRows('1,"Smith, Ada",London', people);
    expect(parsed.rows[0].name).toBe("Smith, Ada");
  });

  it("keeps a doubled quote as one quote", () => {
    const parsed = parsePastedRows('1,"she said ""hi""",London', people);
    expect(parsed.rows[0].name).toBe('she said "hi"');
  });

  it("does not tear a row in half on a line break inside a cell", () => {
    const parsed = parsePastedRows('1,"two\nlines",London', people);

    expect(parsed.rows).toHaveLength(1);
    expect(parsed.rows[0].name).toBe("two\nlines");
  });

  it("reads an empty cell as null, not as the empty string", () => {
    // A spreadsheet has no way to say "null", and a blank in a date column is not "".
    const parsed = parsePastedRows("1,,London", people);
    expect(parsed.rows[0].name).toBeNull();
  });

  it("survives windows line endings and a trailing newline", () => {
    const parsed = parsePastedRows("1,ada,London\r\n2,grace,Berlin\r\n", people);
    expect(parsed.rows).toHaveLength(2);
  });

  it("stops at the columns the table has", () => {
    const parsed = parsePastedRows("1,ada,London,extra", columns("id", "name"));
    expect(parsed.rows).toEqual([{ id: "1", name: "ada" }]);
  });

  it("says which columns it filled", () => {
    const parsed = parsePastedRows("1,ada", people);
    expect(parsed.columns).toEqual(["id", "name"]);
  });

  it("has nothing to say about nothing", () => {
    expect(parsePastedRows("", people).rows).toEqual([]);
    expect(parsePastedRows("   \n  ", people).rows).toEqual([]);
  });
});
