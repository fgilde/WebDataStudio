export interface FlatTable { columns: string[]; rows: unknown[][] }

/// Turns flat documents into a table. Column order follows the first document; a key another
/// document lacks becomes null rather than shifting the row sideways.
export function flattenDocuments(documents: unknown[]): FlatTable {
  const columns: string[] = [];

  for (const document of documents) {
    if (!isPlainObject(document)) continue;
    for (const key of Object.keys(document)) if (!columns.includes(key)) columns.push(key);
  }

  if (columns.length === 0)
    return { columns: ["value"], rows: documents.map(d => [d]) };

  const rows = documents.map(document => {
    if (!isPlainObject(document)) return columns.map((_, i) => (i === 0 ? document : null));
    return columns.map(key => (key in document ? document[key] : null));
  });

  return { columns, rows };
}

/// True when every document is shallow, which is when a table view is honest about the data.
export function isFlat(documents: unknown[]): boolean {
  return documents.every(document =>
    isPlainObject(document) &&
    Object.values(document).every(v => v === null || typeof v !== "object"));
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
