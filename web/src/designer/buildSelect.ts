import type { DialectId } from "../sql/splitStatements";

export type JoinKind = "INNER" | "LEFT" | "RIGHT" | "FULL";

export interface QueryTable { name: string; schema?: string; alias: string }
export interface QueryJoin {
  left: string; right: string; leftColumn: string; rightColumn: string; kind: JoinKind;
}
export interface QueryColumn { table: string; column: string; aggregate?: string; alias?: string }
export interface QueryFilter { table: string; column: string; operator: string; value: string }
export interface QueryOrder { table: string; column: string; descending: boolean }

export interface QueryModel {
  tables: QueryTable[];
  joins: QueryJoin[];
  columns: QueryColumn[];
  filters: QueryFilter[];
  grouping: boolean;
  order: QueryOrder[];
  limit?: number;
}

export const emptyModel = (): QueryModel =>
  ({ tables: [], joins: [], columns: [], filters: [], grouping: false, order: [] });

const quote = (name: string, dialect: DialectId): string => {
  if (dialect === "mysql") return "`" + name.replace(/`/g, "``") + "`";
  if (dialect === "sqlserver") return "[" + name.replace(/]/g, "]]") + "]";
  return '"' + name.replace(/"/g, '""') + '"';
};

/// SQL Server before 2012 used TOP; every engine here supports one of these two, and OFFSET/FETCH
/// is the portable spelling on the SQL Server side.
const paging = (limit: number, dialect: DialectId) =>
  dialect === "sqlserver" ? `OFFSET 0 ROWS FETCH NEXT ${limit} ROWS ONLY` : `LIMIT ${limit}`;

/// One direction only: model to SQL. Parsing arbitrary SQL back into a model is a different
/// project, so the designer hands its result to a query tab and lets go.
export function buildSelect(model: QueryModel, dialect: DialectId): string {
  if (model.tables.length === 0 || model.columns.length === 0) return "";

  const q = (name: string) => quote(name, dialect);
  const qualified = (table: QueryTable) =>
    (table.schema ? `${q(table.schema)}.` : "") + q(table.name);

  const reference = (column: { table: string; column: string }) =>
    `${q(column.table)}.${q(column.column)}`;

  const select = model.columns.map(column => {
    const body = column.aggregate
      ? `${column.aggregate}(${column.column === "*" ? "*" : reference(column)})`
      : reference(column);
    return column.alias ? `${body} AS ${q(column.alias)}` : body;
  });

  const [first, ...rest] = model.tables;
  const from = [`${qualified(first)} ${q(first.alias)}`];

  // A table with no join is a cross join; spelling it out beats a silent cartesian product.
  const joined = new Set([first.alias]);

  for (const join of model.joins) {
    const target = model.tables.find(t => t.alias === join.right);
    if (!target) continue;

    from.push(`${join.kind} JOIN ${qualified(target)} ${q(target.alias)} ` +
      `ON ${q(join.left)}.${q(join.leftColumn)} = ${q(join.right)}.${q(join.rightColumn)}`);
    joined.add(join.right);
  }

  for (const table of rest)
    if (!joined.has(table.alias)) from.push(`CROSS JOIN ${qualified(table)} ${q(table.alias)}`);

  const lines = [`SELECT ${select.join(", ")}`, `  FROM ${from.join("\n  ")}`];

  if (model.filters.length > 0) {
    const conditions = model.filters.map((filter, index) => {
      const column = reference(filter);
      if (filter.operator === "IS NULL" || filter.operator === "IS NOT NULL")
        return `${column} ${filter.operator}`;

      // A parameter, not a literal: the designer must not build an injection for the user.
      const marker = dialect === "sqlserver" || dialect === "mysql" ? "@" : ":";
      return `${column} ${filter.operator} ${marker}p${index + 1}`;
    });

    lines.push(` WHERE ${conditions.join("\n   AND ")}`);
  }

  if (model.grouping) {
    const plain = model.columns.filter(c => !c.aggregate).map(reference);
    if (plain.length > 0) lines.push(` GROUP BY ${plain.join(", ")}`);
  }

  if (model.order.length > 0)
    lines.push(` ORDER BY ${model.order
      .map(o => `${reference(o)}${o.descending ? " DESC" : ""}`).join(", ")}`);

  if (model.limit && model.limit > 0) lines.push(` ${paging(model.limit, dialect)}`);

  return lines.join("\n") + ";";
}

/// The values for the parameters buildSelect emitted, in the same order.
export const filterParameters = (model: QueryModel): Record<string, string> =>
  Object.fromEntries(model.filters
    .filter(f => f.operator !== "IS NULL" && f.operator !== "IS NOT NULL")
    .map((f, index) => [`p${index + 1}`, f.value]));
