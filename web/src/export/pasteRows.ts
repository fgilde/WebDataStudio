import type { QueryColumn } from "../query/runQuery";

/// Rows on their way *in*: what somebody copied out of a spreadsheet, a CSV file or another grid,
/// turned into inserts this table would accept. Pure, so the awkward cases — a quoted comma, a
/// header that is really data, a column this table does not have — are cheap to state in a test.

export interface PastedRows {
  /// One record per line, keyed by column name. Ready for the change set's insertRow.
  rows: Record<string, unknown>[];
  /// The columns that were actually filled, in the order they were read.
  columns: string[];
  /// Which columns the paste carried and this table does not have; named rather than swallowed.
  ignored: string[];
  /// True when the first line was read as a header rather than as data.
  usedHeader: boolean;
}

/// Excel and every database grid copy tab-separated. A file somebody opened in an editor is more
/// likely comma-separated. Whichever appears in the first line wins; a single column has neither
/// and does not care.
function delimiterOf(line: string): string {
  return line.includes("\t") ? "\t" : ",";
}

/// One line into its cells, respecting the quoting CSV uses: "a,b" is one cell, "" is a quote.
function cells(line: string, delimiter: string): string[] {
  const out: string[] = [];
  let value = "";
  let quoted = false;

  for (let i = 0; i < line.length; i++) {
    const c = line[i];

    if (quoted) {
      if (c === '"' && line[i + 1] === '"') { value += '"'; i++; continue; }
      if (c === '"') { quoted = false; continue; }
      value += c;
      continue;
    }

    if (c === '"' && value === "") { quoted = true; continue; }
    if (c === delimiter) { out.push(value); value = ""; continue; }
    value += c;
  }

  out.push(value);
  return out;
}

/// A line break inside a quoted cell is part of the cell, not the end of the row. Splitting on
/// newlines first would tear those rows in half, so the split counts quotes as it goes.
function lines(text: string): string[] {
  const out: string[] = [];
  let current = "";
  let quoted = false;

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"') quoted = !quoted;

    if (!quoted && (c === "\n" || c === "\r")) {
      if (c === "\r" && text[i + 1] === "\n") i++;
      out.push(current);
      current = "";
      continue;
    }

    current += c;
  }

  if (current !== "") out.push(current);

  // A line of nothing but spaces is not a row of empty cells: ",,," says three empty cells and is
  // kept, "   " says somebody's selection ran one line too far and is not.
  return out.filter(line => line.trim() !== "");
}

/// What an empty cell means. A spreadsheet has no way to say "null", and a blank in a numeric or
/// date column is far more likely to mean "nothing here" than "the empty string" — so it is null,
/// and the preview shows exactly that before anything is written.
const cellValue = (raw: string): unknown => (raw === "" ? null : raw);

export function parsePastedRows(text: string, columns: QueryColumn[]): PastedRows {
  const rows: Record<string, unknown>[] = [];
  const names = columns.map(c => c.name);
  const source = lines(text);

  if (source.length === 0) return { rows: [], columns: [], ignored: [], usedHeader: false };

  const delimiter = delimiterOf(source[0]);
  const first = cells(source[0], delimiter).map(c => c.trim());

  // A header only if every cell of it names a column of this table. "id,name" pasted into a table
  // with those columns is a header; "1,ada" is not, and neither is "id,nonsense".
  const byLowerName = new Map(names.map(name => [name.toLowerCase(), name]));
  const usedHeader = first.length > 0 && first.every(c => byLowerName.has(c.toLowerCase()));

  // With a header, each cell goes where its name says. Without one, cells go left to right into
  // the columns as given — which is what pasting into a spreadsheet does.
  const target = usedHeader ? first.map(c => byLowerName.get(c.toLowerCase()) ?? c) : names;
  const ignored = usedHeader ? first.filter(c => !byLowerName.has(c.toLowerCase())) : [];

  for (const line of source.slice(usedHeader ? 1 : 0)) {
    const values = cells(line, delimiter);
    const row: Record<string, unknown> = {};

    for (let i = 0; i < values.length && i < target.length; i++) {
      if (ignored.includes(target[i])) continue;
      row[target[i]] = cellValue(values[i]);
    }

    if (Object.keys(row).length > 0) rows.push(row);
  }

  const filled = target.filter(name => !ignored.includes(name) && rows.some(row => name in row));

  return { rows, columns: filled, ignored, usedHeader };
}
