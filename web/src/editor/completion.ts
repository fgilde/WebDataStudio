export type CompletionContext =
  | { kind: "columns"; table: string }
  | { kind: "tables" }
  | { kind: "any" };

const FROM_JOIN = /\b(?:from|join|update|into)\s+(?:([A-Za-z_][\w$]*)\.)?([A-Za-z_][\w$]*)(?:\s+(?:as\s+)?([A-Za-z_][\w$]*))?/gi;

const KEYWORDS = new Set([
  "on", "where", "group", "order", "having", "limit", "offset", "set", "values",
  "inner", "left", "right", "full", "outer", "cross", "join", "as", "using", "select",
]);

// Maps each alias in the statement to the bare table name it refers to. Also maps a table's own
// name to itself so `users.` completes without an alias.
export function collectAliases(sql: string): Map<string, string> {
  const aliases = new Map<string, string>();
  for (const match of sql.matchAll(FROM_JOIN)) {
    const table = match[2];
    const alias = match[3];
    if (!table) continue;
    aliases.set(table.toLowerCase(), table);
    if (alias && !KEYWORDS.has(alias.toLowerCase())) aliases.set(alias.toLowerCase(), table);
  }
  return aliases;
}

const AFTER_KEYWORDS = /\b(from|join|update|into|table)\s+[\w."]*$/i;

export function completionContext(sql: string, offset: number): CompletionContext {
  const before = sql.slice(0, offset);

  const dotted = before.match(/([A-Za-z_][\w$]*)\.\s*[\w$]*$/);
  if (dotted) {
    const table = collectAliases(sql).get(dotted[1].toLowerCase());
    if (table) return { kind: "columns", table };
  }

  if (AFTER_KEYWORDS.test(before)) return { kind: "tables" };
  return { kind: "any" };
}

export const SQL_KEYWORDS = [
  "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
  "INSERT INTO", "UPDATE", "DELETE FROM", "VALUES", "SET", "JOIN", "LEFT JOIN",
  "RIGHT JOIN", "INNER JOIN", "FULL JOIN", "ON", "AS", "DISTINCT", "COUNT", "SUM",
  "AVG", "MIN", "MAX", "CASE", "WHEN", "THEN", "ELSE", "END", "AND", "OR", "NOT",
  "NULL", "IS NULL", "IS NOT NULL", "IN", "EXISTS", "BETWEEN", "LIKE", "UNION",
  "UNION ALL", "WITH", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE INDEX",
];
