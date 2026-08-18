import type { QueryColumn } from "../query/runQuery";

// Pure string builders, no DOM: the components call navigator.clipboard with the result, which
// keeps these testable without a browser.

const text = (value: unknown): string =>
  value === null || value === undefined ? "" : String(value);

export function copyAsCsv(rows: unknown[][], columns: QueryColumn[]): string {
  const escape = (value: string) =>
    /[",\n\r]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;

  const header = columns.map(c => escape(c.name)).join(",");
  const body = rows.map(row => row.map(v => escape(text(v))).join(","));
  return [header, ...body].join("\n");
}

export function copyAsJson(rows: unknown[][], columns: QueryColumn[]): string {
  const objects = rows.map(row =>
    Object.fromEntries(columns.map((c, i) => [c.name, row[i] ?? null])));
  return JSON.stringify(objects, null, 2);
}

/// The values of one selected column, ready to paste into an IN (…) clause.
export function copyAsSqlInList(values: unknown[]): string {
  return values.map(value => {
    if (value === null || value === undefined) return "NULL";
    if (typeof value === "number") return String(value);
    if (typeof value === "boolean") return value ? "TRUE" : "FALSE";
    return `'${String(value).replace(/'/g, "''")}'`;
  }).join(", ");
}

export function copyAsMarkdown(rows: unknown[][], columns: QueryColumn[]): string {
  const cell = (value: string) => value.replace(/\|/g, "\\|").replace(/\r?\n/g, "<br>");

  const header = `| ${columns.map(c => cell(c.name)).join(" | ")} |`;
  const separator = `| ${columns.map(() => "---").join(" | ")} |`;
  const body = rows.map(row => `| ${row.map(v => cell(text(v))).join(" | ")} |`);
  return [header, separator, ...body].join("\n");
}
