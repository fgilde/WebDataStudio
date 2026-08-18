export interface ChartSuggestion {
  kind: "bar" | "line" | "pie";
  labelColumn: number;
  valueColumns: number[];
}

export interface ChartColumn { name: string; dataType: string }

const NUMERIC = /int|dec|num|float|double|real|money|serial/i;
const TEMPORAL = /date|time|timestamp/i;

const looksNumeric = (column: ChartColumn, rows: unknown[][], index: number) => {
  if (NUMERIC.test(column.dataType)) return true;
  // Some engines hand every column over as text; fall back to the values themselves.
  const sample = rows.slice(0, 20).map(r => r[index]).filter(v => v !== null && v !== undefined);
  return sample.length > 0 && sample.every(v => typeof v === "number" || Number(String(v)) === Number(String(v)));
};

const looksTemporal = (column: ChartColumn) => TEMPORAL.test(column.dataType);

/// A default, never a decision: the chart panel lets the user override every part of this.
export function inferChart(columns: ChartColumn[], rows: unknown[][]): ChartSuggestion | null {
  if (columns.length === 0 || rows.length === 0) return null;

  const numericColumns = columns
    .map((c, i) => ({ column: c, index: i }))
    .filter(x => looksNumeric(x.column, rows, x.index));

  const otherColumns = columns
    .map((c, i) => ({ column: c, index: i }))
    .filter(x => !numericColumns.some(n => n.index === x.index));

  if (numericColumns.length === 0) return null;

  // A single numeric column with few rows is a share-of-total question.
  if (columns.length === 1)
    return rows.length <= 12
      ? { kind: "pie", labelColumn: 0, valueColumns: [0] }
      : { kind: "bar", labelColumn: 0, valueColumns: [0] };

  const temporal = otherColumns.find(x => looksTemporal(x.column));
  if (temporal) return {
    kind: "line", labelColumn: temporal.index, valueColumns: numericColumns.map(x => x.index),
  };

  const label = otherColumns[0];
  if (!label) return {
    // Every column is numeric: the first one becomes the axis.
    kind: "line", labelColumn: numericColumns[0].index,
    valueColumns: numericColumns.slice(1).map(x => x.index),
  };

  if (numericColumns.length === 1 && rows.length <= 8)
    return { kind: "pie", labelColumn: label.index, valueColumns: [numericColumns[0].index] };

  return { kind: "bar", labelColumn: label.index, valueColumns: numericColumns.map(x => x.index) };
}
