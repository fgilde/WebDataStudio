import type { DataColumnDto } from "../api";

/// Following a table: which column says what is new, and which rows are.
///
/// Watch mode already re-runs a query and highlights what moved. A table wants the same thing said
/// differently: rows arrive at the top and the new ones should be visible for a moment. The parts
/// worth testing are pure — which columns can order a tail, and which rows on a page were not there
/// the last time it was fetched.

/// A column that can order a tail: a timestamp, a date, or an increasing key. Anything else would
/// scroll to a random place and call it "latest".
/// The column the server selects for a table with no key at all: the row's physical address.
/// It addresses the row and means nothing to a reader, so the grid keeps it and does not show it.
export const ROW_ADDRESS = "wds_row_address";

/// The rows and columns as a person sees them: everything except the physical address the server
/// selected to be able to write a keyless table. Copying or exporting it would put a column nobody
/// asked for — and nobody can use — into the file.
export function withoutAddress<C extends { name: string }>(rows: unknown[][], columns: C[]):
  { rows: unknown[][]; columns: C[] } {
  const at = columns.findIndex(column => column.name === ROW_ADDRESS);
  if (at < 0) return { rows, columns };

  return {
    columns: columns.filter((_, index) => index !== at),
    rows: rows.map(row => row.filter((_, index) => index !== at)),
  };
}

export function followColumns(columns: DataColumnDto[], keyColumns: string[] = []): string[] {
  const temporal = columns.filter(column => /date|time|stamp/i.test(column.dataType));

  // A numeric key, and only a key: a foreign key is numeric and named `person_id` and increases in
  // no particular order, so following it would show a random slice and call it latest.
  const counters = columns.filter(column =>
    /int|serial|bigint|number|decimal/i.test(column.dataType)
    && (keyColumns.includes(column.name) || /seq(uence)?$/i.test(column.name)));

  // Temporal first: "the newest by time" is what somebody means by following a table.
  return [...new Set([...temporal.map(c => c.name), ...counters.map(c => c.name)])];
}

/// The best guess for the column to follow, or null when nothing here can order a tail.
export const suggestFollowColumn = (
  columns: DataColumnDto[], keyColumns: string[] = []): string | null =>
  followColumns(columns, keyColumns)[0] ?? null;

/// One row's identity for the purpose of following: the key columns where there are any, otherwise
/// the whole row. Two rows that differ in nothing are the same row as far as a tail is concerned.
export function rowKey(row: unknown[], columns: DataColumnDto[], keyColumns: string[]): string {
  const indexes = keyColumns.length > 0
    ? keyColumns.map(name => columns.findIndex(column => column.name === name)).filter(i => i >= 0)
    : columns.map((_, index) => index);

  return indexes.map(index => String(row[index] ?? "")).join("");
}

/// Which rows on this page were not on the previous one, by index. `seen` is updated in place: a
/// tail that ran all afternoon must not grow without bound, so it is capped.
export function newRows(rows: unknown[][], columns: DataColumnDto[], keyColumns: string[],
  seen: Set<string>, cap = 5000): Set<number> {
  const fresh = new Set<number>();

  // The first fetch is not "everything is new": that would flash the whole page for no reason.
  const first = seen.size === 0;

  rows.forEach((row, index) => {
    const key = rowKey(row, columns, keyColumns);

    if (!seen.has(key)) {
      if (!first) fresh.add(index);
      seen.add(key);
    }
  });

  if (seen.size > cap) {
    // Oldest first: a Set keeps insertion order, so the front of it is what has been around longest.
    const excess = seen.size - cap;
    let dropped = 0;

    for (const key of seen) {
      seen.delete(key);
      if (++dropped >= excess) break;
    }
  }

  return fresh;
}
