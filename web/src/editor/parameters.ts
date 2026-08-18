import type { DialectId } from "../sql/splitStatements";

/// Every engine spells a bind variable differently, and the marker character means something else
/// in a cast or an operator, so this is a scanner rather than a regular expression.
const MARKER: Record<DialectId | "mongodb" | "redis", string> = {
  postgresql: ":", oracle: ":", sqlite: "$",
  sqlserver: "@", mysql: "@", duckdb: "$", clickhouse: "{",
  mongodb: "", redis: "",
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
