export interface SelectionSummary {
  count: number; numeric: number;
  sum: number | null; avg: number | null; min: number | null; max: number | null;
}

// The status-bar summary of a grid selection. Numeric strings count as numbers because most
// drivers return DECIMAL as a string to avoid precision loss; booleans deliberately do not, so a
// bit column does not silently read as ones and zeroes.
export function summarizeSelection(values: unknown[]): SelectionSummary {
  const numbers: number[] = [];
  for (const value of values) {
    if (value === null || value === undefined || value === "" || typeof value === "boolean") continue;
    const n = typeof value === "number" ? value : Number(value);
    if (Number.isFinite(n)) numbers.push(n);
  }

  if (numbers.length === 0)
    return { count: values.length, numeric: 0, sum: null, avg: null, min: null, max: null };

  const sum = numbers.reduce((a, b) => a + b, 0);
  return {
    count: values.length,
    numeric: numbers.length,
    sum,
    avg: sum / numbers.length,
    min: Math.min(...numbers),
    max: Math.max(...numbers),
  };
}
