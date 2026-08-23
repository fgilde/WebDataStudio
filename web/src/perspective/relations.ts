import type { DiagramDto } from "../api";

/// One step you can take from a row: to the single row a foreign key points at, or to the many rows
/// that point back at it.
export interface Relation {
  /// "out" follows the key this table holds and lands on one row; "in" is every row of another
  /// table that points here.
  direction: "out" | "in";
  /// The table on the other side, qualified as the diagram names it ("public.orders").
  table: string;
  /// Shown in the menu: "customer" or "orders (customer_id)".
  label: string;
  /// The column on this side whose value identifies the other rows.
  from: string;
  /// The column on the other side to compare it against.
  to: string;
}

const unqualified = (table: string) => table.slice(table.lastIndexOf(".") + 1);

/// Every step available from one table, read off the schema's foreign-key graph — the same graph
/// the ER diagram draws.
///
/// Only single-column keys: a composite key cannot be followed by comparing one value, and
/// pretending otherwise would quietly show the wrong rows.
export function relationsOf(diagram: DiagramDto, table: string): Relation[] {
  const relations: Relation[] = [];

  for (const edge of diagram.edges) {
    if (!edge.resolved) continue;
    if (edge.sourceColumns.length !== 1 || edge.targetColumns.length !== 1) continue;

    if (edge.source.toLowerCase() === table.toLowerCase())
      relations.push({
        direction: "out",
        table: edge.target,
        label: `${unqualified(edge.target)} (${edge.sourceColumns[0]})`,
        from: edge.sourceColumns[0],
        to: edge.targetColumns[0],
      });

    if (edge.target.toLowerCase() === table.toLowerCase())
      relations.push({
        direction: "in",
        table: edge.source,
        label: `${unqualified(edge.source)} (${edge.sourceColumns[0]})`,
        from: edge.targetColumns[0],
        to: edge.sourceColumns[0],
      });
  }

  // A table related twice over different columns appears twice, which is the truth; the same
  // relation read from both ends does not.
  return relations.filter((relation, index) => relations.findIndex(other =>
    other.direction === relation.direction && other.table === relation.table
    && other.from === relation.from && other.to === relation.to) === index);
}

/// The object reference for a qualified diagram name.
export const refOfTable = (table: string) => {
  const dot = table.lastIndexOf(".");
  return dot < 0 ? `Table:${table}` : `Table:${table.slice(0, dot)}/${table.slice(dot + 1)}`;
};

/// The filter that selects the rows on the other side. Written in the filter language, so the
/// server parses it the same way it parses one typed by hand — and a value with a space or a comma
/// in it survives.
export const filterForValue = (value: unknown): string => {
  if (value === null || value === undefined) return "NULL";

  const text = String(value);
  return /^[\w.@-]+$/.test(text) ? `=${text}` : `="${text.replaceAll('"', '""')}"`;
};
