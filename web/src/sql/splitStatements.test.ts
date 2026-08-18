import { describe, expect, it } from "vitest";
import { splitStatements, statementAt } from "./splitStatements";

const texts = (sql: string, dialect: "postgresql" | "sqlserver" = "postgresql") =>
  splitStatements(sql, dialect).map(s => s.text.trim());

describe("splitStatements", () => {
  it("splits on semicolons", () => expect(texts("SELECT 1; SELECT 2;")).toEqual(["SELECT 1", "SELECT 2"]));
  it("ignores a semicolon in a string", () => expect(texts("SELECT 'a;b'")).toHaveLength(1));
  it("ignores a semicolon in a line comment", () => expect(texts("SELECT 1 -- a;b\n")).toHaveLength(1));
  it("ignores a semicolon in a block comment", () => expect(texts("SELECT /* a;b */ 1")).toHaveLength(1));
  it("ignores a semicolon in a quoted identifier", () => expect(texts('SELECT "we;ird" FROM t')).toHaveLength(1));

  it("keeps a dollar-quoted body intact", () =>
    expect(texts("CREATE FUNCTION f() AS $$ SELECT 1; $$ LANGUAGE sql;")).toHaveLength(1));

  it("splits sql server batches on GO", () =>
    expect(texts("SELECT 1\nGO\nSELECT 2", "sqlserver")).toEqual(["SELECT 1", "SELECT 2"]));

  it("does not treat GO inside an identifier as a separator", () =>
    expect(texts("SELECT going FROM t", "sqlserver")).toHaveLength(1));

  it("drops empty statements", () => expect(texts("SELECT 1;;;")).toHaveLength(1));

  it("reports character offsets", () => {
    const [first, second] = splitStatements("SELECT 1;\nSELECT 2;", "postgresql");
    expect(first.start).toBe(0);
    expect(second.start).toBeGreaterThan(first.end);
  });
});

describe("statementAt", () => {
  const sql = "SELECT 1;\nSELECT 2;";

  it("finds the statement containing the cursor", () =>
    expect(statementAt(sql, 12, "postgresql")?.text.trim()).toBe("SELECT 2"));

  it("returns the preceding statement when the cursor sits on the terminator", () =>
    // offset 8 is the ';' of the first statement; 9 is already the newline, which belongs to the next
    expect(statementAt(sql, 8, "postgresql")?.text.trim()).toBe("SELECT 1"));

  it("returns the last statement when the cursor is past the end", () =>
    expect(statementAt(sql, 99, "postgresql")?.text.trim()).toBe("SELECT 2"));

  it("returns null for empty input", () => expect(statementAt("   ", 1, "postgresql")).toBeNull());
});
