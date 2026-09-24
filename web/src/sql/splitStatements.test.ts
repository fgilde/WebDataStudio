import { describe, expect, it } from "vitest";
import { framedRange, splitStatements, statementAt, textToRun } from "./splitStatements";

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

describe("framedRange", () => {
  // The frame shows what Ctrl+Enter runs; the line break after the previous semicolon is not part
  // of it, or the frame would start on the DECLARE line above.
  it("starts at the statement's first character and ends at its last", () => {
    const sql = "DECLARE @a int = 1;\n\n  SELECT @a\n  FROM t\n\n";
    const statement = statementAt(sql, sql.indexOf("FROM"), "sqlserver")!;
    const { start, end } = framedRange(sql, statement);

    expect(sql.slice(start, end)).toBe("SELECT @a\n  FROM t");
  });
});

describe("framedRange with comments", () => {
  it("starts at the code, not at the comments in front of it", () => {
    const sql = "DECLARE @a int = 1;\n-- DROP TABLE x;\n/* note */\n  SELECT @a";
    const statement = statementAt(sql, sql.indexOf("@a", 30), "sqlserver")!;
    const { start, end } = framedRange(sql, statement);

    expect(sql.slice(start, end)).toBe("SELECT @a");
  });

  it("keeps a statement that is nothing but a comment", () => {
    const sql = "-- only a note";
    const { start, end } = framedRange(sql, statementAt(sql, 3, "sqlserver")!);
    expect(sql.slice(start, end)).toBe("-- only a note");
  });
});

describe("statementAt without semicolons", () => {
  // Rider reads a blank line before a new statement keyword as the end of the statement above.
  const sql = [
    "SELECT S.Id",
    "",
    "FROM dbo.Shipments S",
    "WHERE S.Id IN (",
    "",
    "  SELECT 1",
    ")",
    "UNION ALL",
    "SELECT 2",
    "",
    "",
    " SELECT COUNT(*) FROM t",
  ].join("\n");

  it("gives the last query a statement of its own", () => {
    expect(statementAt(sql, sql.indexOf("COUNT"), "sqlserver")?.text.trim()).toBe("SELECT COUNT(*) FROM t");
  });

  it("keeps a blank line before FROM, inside parentheses and a UNION in one statement", () => {
    const first = statementAt(sql, sql.indexOf("UNION"), "sqlserver")!.text.trim();
    expect(first.startsWith("SELECT S.Id")).toBe(true);
    expect(first.endsWith("SELECT 2")).toBe(true);
    expect(statementAt(sql, 2, "sqlserver")!.text.trim()).toBe(first);
  });

  it("is not fooled by a keyword in a comment or a string after a blank line", () => {
    const text = "SELECT 'a\n\nSELECT b'\n\n-- note\nFROM t";
    expect(statementAt(text, text.length - 1, "sqlserver")?.text.trim()).toBe(text);
  });

  it("takes the cursor at the very start of the last query's line as being in it", () => {
    const lineStart = sql.lastIndexOf("\n") + 1;
    expect(statementAt(sql, lineStart, "sqlserver")?.text.trim()).toBe("SELECT COUNT(*) FROM t");
  });

  it("still splits at semicolons as before", () => {
    const text = "SELECT 1;\nSELECT 2";
    expect(statementAt(text, text.length, "sqlserver")?.text.trim()).toBe("SELECT 2");
  });
});

describe("textToRun", () => {
  const sql = "SELECT 1;\nSELECT 2;\n\nSELECT 3";

  // The Run button and Ctrl+Enter run the same thing: the selection, else the statement under the
  // cursor — only "Run script" runs everything.
  it("runs the selection when there is one", () => {
    expect(textToRun(sql, "SELECT 2", 0, "sqlserver")).toBe("SELECT 2");
  });

  it("runs the statement under the cursor otherwise", () => {
    expect(textToRun(sql, null, sql.indexOf("2"), "sqlserver")?.trim()).toBe("SELECT 2");
  });

  it("runs nothing in an empty editor", () => {
    expect(textToRun("  ", null, 0, "sqlserver")).toBeNull();
  });
});
