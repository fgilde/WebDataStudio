import type { DialectId } from "../sql/splitStatements";
import { markerFor } from "../editor/parameters";

export type JoinKind = "INNER" | "LEFT" | "RIGHT" | "FULL";

export interface QueryTable { name: string; schema?: string; alias: string }
export interface QueryJoin {
  left: string; right: string; leftColumn: string; rightColumn: string; kind: JoinKind;
}
export interface QueryColumn { table: string; column: string; aggregate?: string; alias?: string }
export interface QueryFilter {
  table: string; column: string; operator: string; value: string;
  /// How this condition joins the one before it. Absent means AND, which is all older models knew;
  /// the first filter's is ignored.
  connector?: "AND" | "OR";
  /// Set on a HAVING entry: the condition applies to the aggregate, not the column.
  aggregate?: string;
}
export interface QueryOrder { table: string; column: string; descending: boolean }

/// "there is a row over there" / "there is none": the condition a join cannot express, because a
/// join that finds nothing removes the row instead of keeping it.
export interface QueryExists {
  /// The table the subquery reads. It is only in the subquery — not one of the query's own tables.
  name: string;
  schema?: string;
  /// The side in the outer query the subquery is tied to.
  outerTable: string;
  outerColumn: string;
  /// The column on the inner table to compare against.
  column: string;
  negated: boolean;
}

export interface QueryModel {
  tables: QueryTable[];
  joins: QueryJoin[];
  columns: QueryColumn[];
  filters: QueryFilter[];
  /// Forces a GROUP BY even without an aggregate; an aggregate implies one anyway.
  grouping: boolean;
  having?: QueryFilter[];
  distinct?: boolean;
  exists?: QueryExists[];
  order: QueryOrder[];
  limit?: number;
}

/// A table the designer has loaded: the model's identity plus what the server told us about it.
/// Foreign keys are the reason this exists — a join the schema already knows should not have to be
/// typed by hand.
export interface LoadedTable {
  alias: string;
  name: string;
  schema?: string;
  columns: string[];
  foreignKeys: ForeignKey[];
}

/// The shape `describeObject` returns; declared here so this module stays free of the API types.
export interface ForeignKey {
  name: string;
  columns: string[];
  referencedSchema: string;
  referencedTable: string;
  referencedColumns: string[];
  onDelete: string;
  onUpdate: string;
}

export interface SuggestedJoin {
  left: string; leftColumn: string; right: string; rightColumn: string; kind: JoinKind;
  /// Further column pairs of a composite key. The first pair is the join itself; these belong in
  /// the ON clause too, and the designer offers them as filters.
  extra?: { leftColumn: string; rightColumn: string }[];
}

/// Reads the foreign keys of both tables and returns the join between them, whichever side happens
/// to hold the key. Null when the two are unrelated — a cross join is then the honest answer, and
/// the user can still type a condition.
export function suggestJoin(left: LoadedTable, right: LoadedTable): SuggestedJoin | null {
  // The child holds the key, so try both directions and keep the orientation of the arguments.
  const fromRight = match(right, left);
  if (fromRight)
    return {
      left: left.alias, leftColumn: fromRight.parentColumns[0],
      right: right.alias, rightColumn: fromRight.childColumns[0],
      kind: "INNER",
      ...pairs(fromRight.parentColumns, fromRight.childColumns),
    };

  const fromLeft = match(left, right);
  if (fromLeft)
    return {
      left: left.alias, leftColumn: fromLeft.childColumns[0],
      right: right.alias, rightColumn: fromLeft.parentColumns[0],
      kind: "INNER",
      ...pairs(fromLeft.childColumns, fromLeft.parentColumns),
    };

  return null;
}

/// The foreign key on `child` that points at `parent`, if there is one. A schema only has to agree
/// when both sides name one: SQLite and MySQL hand out keys without a schema.
function match(child: LoadedTable, parent: LoadedTable) {
  for (const key of child.foreignKeys) {
    if (key.referencedTable.toLowerCase() !== parent.name.toLowerCase()) continue;
    if (parent.schema && key.referencedSchema
        && key.referencedSchema.toLowerCase() !== parent.schema.toLowerCase()) continue;
    if (key.columns.length === 0 || key.columns.length !== key.referencedColumns.length) continue;

    return { childColumns: key.columns, parentColumns: key.referencedColumns };
  }

  return null;
}

const pairs = (leftColumns: string[], rightColumns: string[]) =>
  leftColumns.length > 1
    ? { extra: leftColumns.slice(1).map((column, index) => ({
        leftColumn: column, rightColumn: rightColumns[index + 1],
      })) }
    : {};

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

  const aggregated = model.columns.some(column => column.aggregate);

  const select = model.columns.map(column => {
    const body = column.aggregate
      ? `${column.aggregate.toUpperCase()}(${column.column === "*" ? "*" : reference(column)})`
      : reference(column);
    return column.alias ? `${body} AS ${q(column.alias)}` : body;
  });

  const tableOf = (alias: string) => model.tables.find(t => t.alias === alias)!;
  const pending = model.joins.filter(join => join.left !== join.right
    && model.tables.some(t => t.alias === join.left) && model.tables.some(t => t.alias === join.right));

  // Each table's line in the FROM, so a later condition between two placed tables finds its ON.
  const from: string[] = [];
  const placed = new Map<string, number>();
  const place = (alias: string, line: string) => { placed.set(alias, from.length); from.push(line); };

  const first = model.tables[0];
  place(first.alias, `${qualified(first)} ${q(first.alias)}`);

  // The order joins were drawn in is not one the engine accepts: an ON clause may only name tables
  // already in the FROM. So every step takes the first join that reaches a placed table, and only
  // when none does is the next table cross joined — spelled out, not a silent cartesian product.
  for (;;) {
    const index = pending.findIndex(join => placed.has(join.left) || placed.has(join.right));

    if (index === -1) {
      const next = model.tables.find(t => !placed.has(t.alias));
      if (!next) break;
      place(next.alias, `CROSS JOIN ${qualified(next)} ${q(next.alias)}`);
      continue;
    }

    const [join] = pending.splice(index, 1);
    const on = `${q(join.left)}.${q(join.leftColumn)} = ${q(join.right)}.${q(join.rightColumn)}`;

    // Both sides already there: a second condition between them, not the same table twice. The
    // later of the two came in through a join, so its line has an ON to extend.
    if (placed.has(join.left) && placed.has(join.right)) {
      from[Math.max(placed.get(join.left)!, placed.get(join.right)!)] += ` AND ${on}`;
      continue;
    }

    // A join drawn towards a table already placed brings in its left side instead, and "keep every
    // row of the left" then reads the other way round.
    const reversed = placed.has(join.right);
    const alias = reversed ? join.left : join.right;
    const kind = !reversed ? join.kind
      : join.kind === "LEFT" ? "RIGHT"
      : join.kind === "RIGHT" ? "LEFT"
      : join.kind;

    place(alias, `${kind} JOIN ${qualified(tableOf(alias))} ${q(alias)} ON ${on}`);
  }

  const lines = [
    `SELECT ${model.distinct ? "DISTINCT " : ""}${select.join(", ")}`,
    `  FROM ${from.join("\n  ")}`,
  ];

  // A parameter, not a literal: the designer must not build an injection for the user. The values
  // travel beside the SQL (filterParameters), into the preview and the query tab's parameter dialog.
  // The editor's own marker, or the query tab would not recognise these as parameters to ask for
  // (SQLite and DuckDB use `$`). ClickHouse's is a typed `{name:Type}` the designer cannot fill in.
  const marker = dialect === "clickhouse" ? ":" : markerFor(dialect);
  const condition =(column: string, entry: QueryFilter, name: string) => {
    if (entry.operator === "IS NULL" || entry.operator === "IS NOT NULL")
      return `${column} ${entry.operator}`;

    return entry.operator === "IN"
      ? `${column} IN (${marker}${name})`
      : `${column} ${entry.operator} ${marker}${name}`;
  };

  const filtered = model.filters.length > 0;
  const where = model.filters
    .map((filter, index) => (index === 0 ? "" : `\n${filter.connector === "OR" ? "    OR" : "   AND"} `)
      + condition(reference(filter), filter, `p${index + 1}`))
    .join("");

  // EXISTS after the plain conditions, whether or not there were any: a query can consist of
  // nothing but "and there is no order for this customer".
  const existing = (model.exists ?? [])
    .filter(entry => entry.name && entry.column && entry.outerColumn);

  // AND binds tighter than OR: an EXISTS after "x OR y" would otherwise only apply to y.
  const ored = model.filters.slice(1).some(filter => filter.connector === "OR");
  if (filtered) lines.push(` WHERE ${ored && existing.length > 0 ? `(${where})` : where}`);

  if (existing.length > 0) {
    const conditions = existing.map((entry, index) => {
      // An alias of its own, so the subquery cannot collide with the outer query's tables.
      const inner = `x${index + 1}`;
      const target = entry.schema ? `${q(entry.schema)}.${q(entry.name)}` : q(entry.name);

      return `${entry.negated ? "NOT " : ""}EXISTS (SELECT 1 FROM ${target} ${q(inner)}`
        + ` WHERE ${q(inner)}.${q(entry.column)} = ${q(entry.outerTable)}.${q(entry.outerColumn)})`;
    });

    lines.push(filtered
      ? `   AND ${conditions.join("\n   AND ")}`
      : ` WHERE ${conditions.join("\n   AND ")}`);
  }

  // An aggregate makes the grouping mandatory: every plain column has to be in GROUP BY, or the
  // engine rejects the statement. The checkbox stays for grouping without an aggregate.
  if (model.grouping || aggregated) {
    const plain = model.columns.filter(c => !c.aggregate).map(reference);
    if (plain.length > 0) lines.push(` GROUP BY ${plain.join(", ")}`);
  }

  if (model.having && model.having.length > 0) {
    const conditions = model.having.map((entry, index) => condition(
      entry.aggregate ? `${entry.aggregate.toUpperCase()}(${reference(entry)})` : reference(entry),
      entry, `h${index + 1}`));

    lines.push(` HAVING ${conditions.join("\n    AND ")}`);
  }

  if (model.order.length > 0)
    lines.push(` ORDER BY ${model.order
      .map(o => `${reference(o)}${o.descending ? " DESC" : ""}`).join(", ")}`);

  if (model.limit && model.limit > 0) lines.push(` ${paging(model.limit, dialect)}`);

  return lines.join("\n") + ";";
}

/// The statement plus the model that produced it, as a trailing comment. This is what goes into a
/// query tab: the builder can reopen its own query later without anybody writing a SQL parser.
/// The live preview uses plain `buildSelect`, because a comment in every preview is noise.
///
/// Filter values are stripped from the comment. They are user input that would otherwise be
/// copied into the SQL text, into the history and into anything the query is pasted in to; the
/// query tab gets them separately, as the parameter dialog's first answers.
export function buildSelectWithModel(model: QueryModel, dialect: DialectId): string {
  const sql = buildSelect(model, dialect);
  if (!sql) return sql;

  const withoutValues: QueryModel = {
    ...model,
    filters: model.filters.map(filter => ({ ...filter, value: "" })),
    having: model.having?.map(entry => ({ ...entry, value: "" })),
  };

  // An alias containing "*/" would close the comment early; "*\/" is the same JSON.
  return `${sql}\n/* wds:model ${JSON.stringify(withoutValues).replace(/\*\//g, "*\\/")} */`;
}

const MODEL_COMMENT = /\/\* wds:model (\{[\s\S]*?\}) \*\//;

/// The model a generated statement carries, or null for anything written by hand.
export function parseModel(sql: string): QueryModel | null {
  const match = MODEL_COMMENT.exec(sql);
  if (!match) return null;

  try {
    const model = JSON.parse(match[1]) as QueryModel;
    return Array.isArray(model.tables) && Array.isArray(model.columns) ? model : null;
  } catch {
    return null;
  }
}

/// The values for the parameters buildSelect emitted, named by position. A null check takes no
/// parameter but still counts, or every name after it would be off by one.
export const filterParameters = (model: QueryModel): Record<string, string> => {
  const valuesOf = (entries: QueryFilter[], prefix: string) => entries.flatMap((f, index) =>
    f.operator === "IS NULL" || f.operator === "IS NOT NULL" ? [] : [[`${prefix}${index + 1}`, f.value]]);

  return Object.fromEntries([...valuesOf(model.filters, "p"), ...valuesOf(model.having ?? [], "h")]);
};
