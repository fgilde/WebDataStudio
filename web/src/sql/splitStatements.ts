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

/// Words a statement starts with. After a blank line, one of these at the start of a line begins a
/// new statement even without a semicolon — how Rider reads a script, and how people write one.
const STATEMENT_START = new Set([
  "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WITH", "DECLARE", "SET", "IF", "WHILE", "BEGIN",
  "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "TRUNCATE", "USE", "PRINT", "RETURN", "CALL",
  "EXPLAIN", "SHOW", "DESCRIBE", "VALUES", "GRANT", "REVOKE", "VACUUM", "ANALYZE", "PRAGMA",
]);

/// A statement cut again where a blank line is followed by a line starting with a statement word,
/// outside strings, comments and parentheses. Only the cursor needs this: the server runs the
/// script as it is written.
function byBlankLines(sql: string, statement: SqlStatement): SqlStatement[] {
  const parts: SqlStatement[] = [];
  let partStart = statement.start;
  let depth = 0;
  let blank = false;
  let lineHasCode = false;
  let i = statement.start;

  const cut = (at: number) => {
    const text = sql.slice(partStart, at);
    if (text.trim().length > 0) parts.push({ text, start: partStart, end: at });
    partStart = at;
  };

  while (i < statement.end) {
    const c = sql[i];

    if (c === "\n") {
      // A line with nothing on it — comments do not count as something.
      if (!lineHasCode && sql.slice(sql.lastIndexOf("\n", i - 1) + 1, i).trim() === "") blank = true;
      lineHasCode = false;
      i++;
      continue;
    }
    if (c === "-" && sql[i + 1] === "-") {
      const newline = sql.indexOf("\n", i);
      i = newline === -1 || newline > statement.end ? statement.end : newline;
      continue;
    }
    if (c === "/" && sql[i + 1] === "*") {
      const close = sql.indexOf("*/", i + 2);
      i = close === -1 ? statement.end : close + 2;
      continue;
    }
    if (/\s/.test(c)) { i++; continue; }

    if (!lineHasCode && blank && depth === 0 && /[A-Za-z]/.test(c)) {
      let end = i;
      while (end < statement.end && /[A-Za-z_]/.test(sql[end])) end++;
      if (STATEMENT_START.has(sql.slice(i, end).toUpperCase())) cut(sql.lastIndexOf("\n", i) + 1);
    }
    lineHasCode = true;
    blank = false;

    if (c === "'" || c === '"' || c === "`" || c === "[") {
      const close = c === "[" ? "]" : c;
      i++;
      while (i < statement.end) {
        if (sql[i] === close && sql[i + 1] === close) { i += 2; continue; }
        if (sql[i] === close) { i++; break; }
        i++;
      }
      continue;
    }
    if (c === "(") depth++;
    else if (c === ")") depth = Math.max(0, depth - 1);
    i++;
  }

  cut(statement.end);
  // A part ends before the line the next one starts on, so a cursor at that line's first column
  // belongs to the next part; `statementAt` counts an end as inside.
  for (let k = 0; k < parts.length - 1; k++) parts[k] = { ...parts[k], end: parts[k + 1].start - 1 };
  return parts.length > 0 ? parts : [statement];
}

// The statement the cursor sits in — what F5 runs when nothing is selected.
export function statementAt(sql: string, offset: number, dialect: DialectId): SqlStatement | null {
  const statements = splitStatements(sql, dialect).flatMap(s => byBlankLines(sql, s));
  if (statements.length === 0) return null;

  for (const s of statements) if (offset >= s.start && offset <= s.end) return s;

  // The cursor is past the last terminator: run the last statement.
  return statements[statements.length - 1];
}

/// The part of a statement worth framing: from its code to its last character, without the line
/// breaks it inherits from the terminator before it or the comments in front of it — as Rider does.
export function framedRange(sql: string, statement: SqlStatement): { start: number; end: number } {
  let end = statement.end;
  while (end > statement.start && /\s/.test(sql[end - 1])) end--;

  let start = statement.start;
  for (;;) {
    while (start < end && /\s/.test(sql[start])) start++;
    const comment = sql.startsWith("--", start) ? sql.indexOf("\n", start)
      : sql.startsWith("/*", start) && sql.includes("*/", start) ? sql.indexOf("*/", start) + 2
        : -1;
    // A statement that is only a comment keeps it: there is nothing else to frame.
    if (comment <= start || comment >= end) break;
    start = comment;
  }
  return { start, end };
}
