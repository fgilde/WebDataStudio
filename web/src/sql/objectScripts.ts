/// Statements the explorer writes into a query tab. They are never run from a menu: the user
/// reads them first and presses F5, which is the whole point of generating rather than executing.
export type ScriptEngine = string;

const quote = (engine: ScriptEngine, name: string): string => {
  if (engine === "mysql") return "`" + name.replace(/`/g, "``") + "`";
  if (engine === "sqlserver") return "[" + name.replace(/]/g, "]]") + "]";
  return '"' + name.replace(/"/g, '""') + '"';
};

// ponytail: the words people actually name tables after, not every engine's full reserved list.
const RESERVED = new Set([
  "all", "and", "as", "asc", "check", "column", "default", "desc", "from", "grant", "group", "index",
  "key", "limit", "not", "null", "offset", "or", "order", "primary", "references", "select", "table",
  "to", "user", "where",
]);

/// A name as the engine reads it back, quoted only where leaving it bare would change it.
/// PostgreSQL folds a bare name to lower case and Oracle to upper case, so `app.Brands` reaches
/// PostgreSQL as `app.brands` — a different table. Everything else only needs quotes for a name
/// that is not a plain identifier or is a keyword.
export function quoteIfNeeded(engine: ScriptEngine, name: string): string {
  const plain =
    engine === "postgresql" ? /^[a-z_][a-z0-9_$]*$/.test(name)
    : engine === "oracle" ? /^[A-Z][A-Z0-9_$#]*$/.test(name)
    : /^[A-Za-z_][A-Za-z0-9_$]*$/.test(name);

  return plain && !RESERVED.has(name.toLowerCase()) ? name : quote(engine, name);
}

/// `schema.table` (or just `table`), each part quoted only if it has to be.
export const qualifiedName = (engine: ScriptEngine, ...parts: string[]): string =>
  parts.filter(Boolean).map(part => quoteIfNeeded(engine, part)).join(".");

export const dropColumn =(engine: ScriptEngine, table: string, column: string) =>
  `ALTER TABLE ${table}\n  DROP COLUMN ${quote(engine, column)};`;

export const selectColumn = (engine: ScriptEngine, table: string, column: string) =>
  engine === "sqlserver"
    ? `SELECT TOP 100 ${quote(engine, column)}\n  FROM ${table};`
    : `SELECT ${quote(engine, column)}\n  FROM ${table}\n LIMIT 100;`;

/// MySQL has no free-standing index namespace, so its DROP names the table too.
export const dropIndex = (engine: ScriptEngine, table: string, index: string) =>
  engine === "mysql"
    ? `DROP INDEX ${quote(engine, index)} ON ${table};`
    : `DROP INDEX ${quote(engine, index)};`;

export function rebuildIndex(engine: ScriptEngine, table: string, index: string): string {
  switch (engine) {
    case "postgresql":
      return `REINDEX INDEX ${quote(engine, index)};`;
    case "sqlserver":
      return `ALTER INDEX ${quote(engine, index)} ON ${table} REBUILD;`;
    case "sqlite":
      return `REINDEX ${quote(engine, index)};`;
    case "mysql":
      // MySQL rebuilds an index by rebuilding the table it belongs to.
      return `-- MySQL rebuilds an index with the table\nOPTIMIZE TABLE ${table};`;
    default:
      return `-- ${engine} has no rebuild statement for an index\n-- index: ${index} on ${table}`;
  }
}

/// MySQL spells the drop after the kind of constraint; everything else takes the name.
export const dropConstraint = (engine: ScriptEngine, table: string, constraint: string) =>
  engine === "mysql"
    ? `ALTER TABLE ${table}\n  DROP FOREIGN KEY ${quote(engine, constraint)};`
    : `ALTER TABLE ${table}\n  DROP CONSTRAINT ${quote(engine, constraint)};`;

export function executeRoutine(engine: ScriptEngine, routine: string): string {
  switch (engine) {
    case "sqlserver":
      return `EXEC ${routine};`;
    case "oracle":
      return `BEGIN\n  ${routine};\nEND;`;
    case "postgresql":
      return `CALL ${routine}();`;
    default:
      return `CALL ${routine}();`;
  }
}
