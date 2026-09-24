import type { DialectId } from "../sql/splitStatements";

/// Every engine spells a bind variable differently, and the marker character means something else
/// in a cast or an operator, so this is a scanner rather than a regular expression.
const MARKER: Record<DialectId | "mongodb" | "redis" | "odata", string> = {
  postgresql: ":", oracle: ":", sqlite: "$",
  sqlserver: "@", mysql: "@", duckdb: "$", clickhouse: "{",
  mongodb: "", redis: "", odata: "",
};

const isNameChar = (c: string) => /[A-Za-z0-9_]/.test(c);

export function markerFor(dialect: string): string {
  return MARKER[dialect as DialectId] ?? "";
}

/// The distinct parameter names, in order of first appearance.
export function findParameters(sql: string, dialect: string): string[] {
  const marker = markerFor(dialect);
  if (!marker) return [];

  const found: string[] = [];
  let i = 0;

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

    if (c === marker) {
      // PostgreSQL's `::type` cast is the classic false positive.
      if (marker === ":" && (sql[i + 1] === ":" || sql[i - 1] === ":")) { i += 2; continue; }
      // `@@version` and MySQL's `@@global` are server variables, not parameters.
      if (marker === "@" && sql[i + 1] === "@") { i += 2; continue; }

      let end = i + 1;
      while (end < sql.length && isNameChar(sql[end])) end++;

      const name = sql.slice(i + 1, end);
      if (name.length > 0 && !found.includes(name)) found.push(name);
      i = end;
      continue;
    }

    i++;
  }

  return found;
}

/// Values are handed to the driver as parameters, never pasted into the SQL — the whole point of
/// naming them. The statement travels unchanged.
export function applyParameters(sql: string, values: Record<string, string | null>, dialect: string):
  { sql: string; parameters: Record<string, string | null> } {
  const names = findParameters(sql, dialect);
  const parameters: Record<string, string | null> = {};

  for (const name of names) parameters[name] = values[name] ?? null;
  return { sql, parameters };
}

/// A variable a T-SQL script declares for itself: `DECLARE @name type [= value]`.
export interface Declaration {
  name: string;
  /// The value as written, or null when the declaration has none.
  value: string | null;
  /// Where the value sits; without one, where the type ends and ` = …` would go.
  start: number;
  end: number;
}

/// Keywords that begin a statement: where a DECLARE without its semicolon has ended.
const STATEMENT_START = new Set([
  "DECLARE", "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WITH", "SET", "IF", "WHILE", "BEGIN",
  "END", "EXEC", "EXECUTE", "RETURN", "PRINT", "DROP", "CREATE", "ALTER", "TRUNCATE", "USE", "GO",
]);

/// SQL Server's DECLAREs, outside strings and comments. MySQL's @variables are session variables
/// set with SET, so there is nothing to find there.
export function findDeclarations(sql: string, dialect: string): Declaration[] {
  if (dialect !== "sqlserver") return [];

  // The index after a string, quoted name or comment that starts at k; -1 when none does.
  const skipAt = (k: number): number => {
    const c = sql[k];
    if (c === "-" && sql[k + 1] === "-") {
      const newline = sql.indexOf("\n", k);
      return newline === -1 ? sql.length : newline;
    }
    if (c === "/" && sql[k + 1] === "*") {
      const close = sql.indexOf("*/", k + 2);
      return close === -1 ? sql.length : close + 2;
    }
    if (c === "'" || c === '"' || c === "[") {
      const close = c === "[" ? "]" : c;
      let j = k + 1;
      while (j < sql.length) {
        if (sql[j] === close && sql[j + 1] === close) { j += 2; continue; }
        if (sql[j] === close) return j + 1;
        j++;
      }
      return sql.length;
    }
    return -1;
  };
  const wordAt = (k: number): string | null => {
    if (!/[A-Za-z_]/.test(sql[k] ?? "") || (k > 0 && (isNameChar(sql[k - 1]) || sql[k - 1] === "@"))) return null;
    let j = k;
    while (j < sql.length && isNameChar(sql[j])) j++;
    return sql.slice(k, j);
  };
  const skipSpace = (k: number): number => {
    while (k < sql.length) {
      const skipped = skipAt(k);
      if (skipped !== -1 && sql[k] !== "'" && sql[k] !== '"' && sql[k] !== "[") { k = skipped; continue; }
      if (!/\s/.test(sql[k])) break;
      k++;
    }
    return k;
  };
  const trimEnd = (from: number, to: number) => {
    while (to > from && /\s/.test(sql[to - 1])) to--;
    return to;
  };
  // Up to the first top-level comma, semicolon, statement keyword — or `=`, while reading a type.
  const scan = (from: number, stopAtEquals: boolean): { at: number; stop: "," | ";" | "=" | "end" } => {
    let depth = 0;
    let k = from;
    while (k < sql.length) {
      const skipped = skipAt(k);
      if (skipped !== -1) { k = skipped; continue; }
      const c = sql[k];
      if (c === "(") depth++;
      else if (c === ")") depth = Math.max(0, depth - 1);
      else if (depth === 0) {
        if (c === "," || c === ";") return { at: k, stop: c };
        if (stopAtEquals && c === "=") return { at: k, stop: "=" };
        const word = wordAt(k);
        if (word) {
          if (STATEMENT_START.has(word.toUpperCase())) return { at: k, stop: "end" };
          k += word.length;
          continue;
        }
      }
      k++;
    }
    return { at: sql.length, stop: "end" };
  };

  const out: Declaration[] = [];
  let i = 0;
  while (i < sql.length) {
    const skipped = skipAt(i);
    if (skipped !== -1) { i = skipped; continue; }

    if (wordAt(i)?.toUpperCase() !== "DECLARE") { i++; continue; }
    i += "DECLARE".length;

    // `DECLARE @a int = 1, @b nvarchar(10), @t TABLE (…)`. A cursor has no @ and ends the list.
    for (;;) {
      i = skipSpace(i);
      if (sql[i] !== "@") break;
      let nameEnd = i + 1;
      while (nameEnd < sql.length && isNameChar(sql[nameEnd])) nameEnd++;
      const name = sql.slice(i + 1, nameEnd);

      const type = scan(nameEnd, true);
      let stop = type.stop;
      i = type.at;
      if (type.stop === "=") {
        const start = skipSpace(type.at + 1);
        const value = scan(start, false);
        const end = trimEnd(start, value.at);
        out.push({ name, value: sql.slice(start, end), start, end });
        stop = value.stop;
        i = value.at;
      } else {
        const end = trimEnd(nameEnd, type.at);
        out.push({ name, value: null, start: end, end });
      }

      if (stop !== ",") break;
      i++;
    }
  }
  return out;
}

const STRING_LITERAL = /^N?'((?:[^']|'')*)'$/s;

/// What a person would type for a declared value: a string without its quotes.
const shown = (value: string) => {
  const literal = STRING_LITERAL.exec(value);
  return literal ? literal[1].replace(/''/g, "'") : value;
};

/// The values the script gives its own variables — what the parameter dialog starts from.
export function declaredValues(sql: string, dialect: string): Record<string, string> {
  const values: Record<string, string> = {};
  for (const d of findDeclarations(sql, dialect)) if (d.value !== null) values[d.name] = shown(d.value);
  return values;
}

/// A value from the dialog, spelled the way its DECLARE spelled the old one.
function literal(value: string, original: string | null): string {
  const text = value.trim();
  if (/^null$/i.test(text)) return "NULL";
  const quoted = original !== null && /^N?'/.test(original);
  if (!quoted && /^-?\d+(\.\d+)?$/.test(text)) return text;
  return `${original?.startsWith("N'") ? "N" : ""}'${value.replace(/'/g, "''")}'`;
}

/// What actually runs. A variable the script declares gets the dialog's value written into its
/// DECLARE and is not sent: a parameter of the same name makes SQL Server refuse the DECLARE as a
/// second declaration. Everything else travels as a parameter, never pasted into the SQL.
export function prepareRun(sql: string, values: Record<string, string | null>, dialect: string):
  { sql: string; parameters: Record<string, string | null> } {
  const declarations = findDeclarations(sql, dialect);
  let text = sql;

  for (const d of [...declarations].reverse()) {
    const value = values[d.name];
    if (value === undefined || value === null) continue;
    if (d.value === null ? value === "" : value === shown(d.value)) continue;

    const written = literal(value, d.value);
    text = text.slice(0, d.start) + (d.value === null ? ` = ${written}` : written) + text.slice(d.end);
  }

  const declared = new Set(declarations.map(d => d.name));
  const parameters: Record<string, string | null> = {};
  for (const name of findParameters(sql, dialect)) if (!declared.has(name)) parameters[name] = values[name] ?? null;
  return { sql: text, parameters };
}

/// What the parameter dialog asks for: the variables the text uses and does not declare itself. A
/// script with its DECLAREs runs as written; a part run without them asks, prefilled from the rest.
export function askedFor(sql: string, dialect: string): string[] {
  const declared = new Set(findDeclarations(sql, dialect).map(d => d.name));
  return findParameters(sql, dialect).filter(name => !declared.has(name));
}
