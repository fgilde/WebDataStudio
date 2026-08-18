export type DialectId =
  | "postgresql" | "mysql" | "sqlserver" | "sqlite" | "oracle" | "duckdb" | "clickhouse";

export interface SqlStatement { text: string; start: number; end: number }

const GO_DIALECTS: DialectId[] = ["sqlserver"];

// Mirrors the server's StatementSplitter. A character scanner: it only tracks strings, comments,
// quoted identifiers and dollar-quoted bodies, which is all that semicolon detection needs.
export function splitStatements(sql: string, dialect: DialectId): SqlStatement[] {
  const out: SqlStatement[] = [];
  const usesGo = GO_DIALECTS.includes(dialect);
  let start = 0;
  let i = 0;

  const flush = (end: number) => {
    const text = sql.slice(start, end);
    if (text.trim().length > 0) out.push({ text, start, end });
    start = end + 1;
  };

  while (i < sql.length) {
    const c = sql[i];

    if (c === "-" && sql[i + 1] === "-") {
      while (i < sql.length && sql[i] !== "\n") i++;
      continue;
    }
    if (c === "/" && sql[i + 1] === "*") {
      const close = sql.indexOf("*/", i + 2);
      i = close === -1 ? sql.length : close + 2;
      continue;
    }
    if (c === "'" || c === '"' || c === "`" || c === "[") {
      const close = c === "[" ? "]" : c;
      i++;
      while (i < sql.length) {
        if (sql[i] === close && sql[i + 1] === close) { i += 2; continue; }
        if (sql[i] === close) { i++; break; }
        i++;
      }
      continue;
    }
    if (c === "$") {
      const tagEnd = sql.indexOf("$", i + 1);
      if (tagEnd > i) {
        const tag = sql.slice(i, tagEnd + 1);
        const end = sql.indexOf(tag, tagEnd + 1);
        if (end > 0) { i = end + tag.length; continue; }
      }
    }
    if (usesGo && (c === "g" || c === "G") && isGoLine(sql, i)) {
      flush(i);
      const newline = sql.indexOf("\n", i);
      if (newline === -1) { i = sql.length; break; }
      start = newline + 1;
      i = newline + 1;
      continue;
    }
    if (c === ";") { flush(i); i++; continue; }
    i++;
  }

  flush(sql.length);
  return out;
}

function isGoLine(sql: string, i: number): boolean {
  const lineStart = sql.lastIndexOf("\n", Math.max(i - 1, 0)) + 1;
  if (sql.slice(lineStart, i).trim() !== "") return false;
  if (sql.slice(i, i + 2).toUpperCase() !== "GO") return false;
  return /^[ \t\r]*(\n|$)/.test(sql.slice(i + 2));
}

// The statement the cursor sits in — what F5 runs when nothing is selected.
export function statementAt(sql: string, offset: number, dialect: DialectId): SqlStatement | null {
  const statements = splitStatements(sql, dialect);
  if (statements.length === 0) return null;

  for (const s of statements) if (offset >= s.start && offset <= s.end) return s;

  // The cursor is past the last terminator: run the last statement.
  return statements[statements.length - 1];
}
