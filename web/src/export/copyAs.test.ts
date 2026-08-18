import { describe, expect, it } from "vitest";
import { copyAsCsv, copyAsJson, copyAsMarkdown, copyAsSqlInList } from "./copyAs";

const columns = [
  { name: "id", dataType: "int", nullable: false },
  { name: "name", dataType: "text", nullable: true },
];

describe("copyAsCsv", () => {
  it("writes a header and rows", () =>
    expect(copyAsCsv([[1, "ada"]], columns)).toBe("id,name\n1,ada"));

  it("quotes a value containing a comma", () =>
    expect(copyAsCsv([[1, "a,b"]], columns)).toContain('"a,b"'));

  it("renders null as an empty field", () =>
    expect(copyAsCsv([[1, null]], columns)).toBe("id,name\n1,"));
});

describe("copyAsJson", () => {
  it("produces an array of objects keyed by column name", () =>
    expect(JSON.parse(copyAsJson([[1, "ada"]], columns))).toEqual([{ id: 1, name: "ada" }]));

  it("keeps null as null", () =>
    expect(JSON.parse(copyAsJson([[1, null]], columns))[0].name).toBeNull());
});

describe("copyAsSqlInList", () => {
  it("leaves numbers bare", () => expect(copyAsSqlInList([1, 2])).toBe("1, 2"));
  it("quotes strings", () => expect(copyAsSqlInList(["a", "b"])).toBe("'a', 'b'"));
  it("doubles an embedded quote", () => expect(copyAsSqlInList(["it's"])).toBe("'it''s'"));
  it("renders null as the keyword", () => expect(copyAsSqlInList([null])).toBe("NULL"));
});

describe("copyAsMarkdown", () => {
  it("writes a table with a separator row", () => {
    const lines = copyAsMarkdown([[1, "ada"]], columns).split("\n");
    expect(lines[0]).toBe("| id | name |");
    expect(lines[1]).toBe("| --- | --- |");
    expect(lines[2]).toBe("| 1 | ada |");
  });

  it("escapes a pipe", () => expect(copyAsMarkdown([[1, "a|b"]], columns)).toContain("a\\|b"));
});
