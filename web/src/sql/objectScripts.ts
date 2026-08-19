/// Statements the explorer writes into a query tab. They are never run from a menu: the user
/// reads them first and presses F5, which is the whole point of generating rather than executing.
export type ScriptEngine = string;

const quote = (engine: ScriptEngine, name: string): string => {
  if (engine === "mysql") return "`" + name.replace(/`/g, "``") + "`";
  if (engine === "sqlserver") return "[" + name.replace(/]/g, "]]") + "]";
  return '"' + name.replace(/"/g, '""') + '"';
};

export const dropColumn = (engine: ScriptEngine, table: string, column: string) =>
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

export const refreshMaterializedView = (engine: ScriptEngine, view: string) =>
  engine === "postgresql"
    ? `REFRESH MATERIALIZED VIEW ${view};`
    : `-- ${engine} refreshes a materialized view its own way\n-- view: ${view}`;
