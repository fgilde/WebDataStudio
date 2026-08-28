export type PivotAggregate = "count" | "sum" | "avg" | "min" | "max";

export interface PivotSpec {
  /// The column whose values become the rows.
  row: string;
  /// The column whose values become the columns. Empty means one column: the total.
  column: string;
  /// What is aggregated. Ignored by `count`, which counts rows.
  value: string;
  aggregate: PivotAggregate;
}

export interface PivotResult {
  /// The values of the column field, in the order they will be shown.
  columns: string[];
  rows: { key: string; cells: (number | null)[]; total: number | null }[];
  /// The bottom row: the same aggregate over each column.
  totals: (number | null)[];
  grand: number | null;
  /// True when the column field had more distinct values than a table can usefully show. The ones
  /// past the cap are left out rather than drawn — a pivot with nine hundred columns is a scroll
  /// bar, not an answer.
  truncated: boolean;
}

/// How many distinct values of the column field are worth drawing.
export const MAX_COLUMNS = 60;

const NOTHING = "(none)";

/// The values of a result, crossed: one column down the side, another across the top, and a number
/// where they meet.
///
/// Grouping already answers "how many per status"; this is the second question — "how many per
/// status *per month*" — which is a table nobody wants to write a GROUP BY for while they are
/// looking at the rows.
export function pivot(columns: { name: string }[], rows: unknown[][], spec: PivotSpec): PivotResult {
  const index = (name: string) => columns.findIndex(column => column.name === name);

  const rowAt = index(spec.row);
  const columnAt = spec.column ? index(spec.column) : -1;
  const valueAt = index(spec.value);

  if (rowAt < 0) return { columns: [], rows: [], totals: [], grand: null, truncated: false };

  // Every bucket keeps the numbers it saw rather than a running total: an average needs both, and a
  // minimum cannot be undone once it has been folded in.
  const buckets = new Map<string, Map<string, number[]>>();
  const columnKeys: string[] = [];
  const rowKeys: string[] = [];

  for (const line of rows) {
    const rowKey = label(line[rowAt]);
    const columnKey = columnAt >= 0 ? label(line[columnAt]) : "";

    if (!buckets.has(rowKey)) {
      buckets.set(rowKey, new Map());
      rowKeys.push(rowKey);
    }

    if (columnAt >= 0 && !columnKeys.includes(columnKey)) columnKeys.push(columnKey);

    const cells = buckets.get(rowKey)!;
    if (!cells.has(columnKey)) cells.set(columnKey, []);

    // count needs no value at all, which is why it is the default: it works on any result.
    if (spec.aggregate === "count") {
      cells.get(columnKey)!.push(1);
    } else if (valueAt >= 0) {
      // A value that is not a number is not counted rather than counted as zero: an average over
      // "the ones that had a number" is an answer, an average that folded in nulls is a lie.
      const number = numberOf(line[valueAt]);
      if (number !== null) cells.get(columnKey)!.push(number);
    }
  }

  const truncated = columnKeys.length > MAX_COLUMNS;
  const shown = columnAt >= 0 ? columnKeys.slice(0, MAX_COLUMNS).sort(compare) : [""];

  const result = rowKeys.sort(compare).map(key => {
    const cells = buckets.get(key)!;
    const values = shown.map(columnKey => fold(cells.get(columnKey) ?? [], spec.aggregate));
    const all = [...cells.values()].flat();

    return { key, cells: values, total: fold(all, spec.aggregate) };
  });

  const totals = shown.map(columnKey =>
    fold(rowKeys.flatMap(key => buckets.get(key)!.get(columnKey) ?? []), spec.aggregate));

  const grand = fold(
    rowKeys.flatMap(key => [...buckets.get(key)!.values()].flat()), spec.aggregate);

  return { columns: shown, rows: result, totals, grand, truncated };
}

function fold(values: number[], aggregate: PivotAggregate): number | null {
  if (values.length === 0) return null;

  switch (aggregate) {
    case "count": return values.length;
    case "sum": return values.reduce((total, one) => total + one, 0);
    case "avg": return values.reduce((total, one) => total + one, 0) / values.length;
    case "min": return Math.min(...values);
    case "max": return Math.max(...values);
  }
}

/// What a value is called in a header. Null is a value people group by, so it gets a name rather
/// than an empty cell nobody can tell from a missing one.
function label(value: unknown): string {
  if (value === null || value === undefined) return NOTHING;
  if (value instanceof Date) return value.toISOString().slice(0, 10);

  return String(value);
}

function numberOf(value: unknown): number | null {
  if (typeof value === "number") return Number.isFinite(value) ? value : null;
  if (typeof value === "boolean") return value ? 1 : 0;

  if (typeof value === "string" && value.trim() !== "") {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

/// Numbers as numbers, dates as dates, everything else as text — so 2, 10 and "2026-01" all land
/// where somebody expects them.
function compare(left: string, right: string): number {
  const a = Number(left);
  const b = Number(right);

  if (Number.isFinite(a) && Number.isFinite(b)) return a - b;
  if (left === NOTHING) return 1;
  if (right === NOTHING) return -1;

  return left.localeCompare(right);
}
