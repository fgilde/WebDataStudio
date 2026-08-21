export type Cell = {
  id: string;
  kind: "sql" | "note";
  text: string;
  connectionId?: string;
};

export interface Notebook {
  id: string;
  name: string;
  cells: Cell[];
}

let counter = 0;

/// Ids only have to be unique inside one notebook, and they must not depend on the clock: a
/// round trip through Markdown has to produce the same document twice.
export const newId = () => `c${++counter}`;

export const newCell = (kind: Cell["kind"], connectionId?: string): Cell =>
  ({ id: newId(), kind, text: "", connectionId });

/// The document, as Markdown somebody can read in a pull request. A SQL cell is a fenced block
/// whose info string carries the connection it ran against; everything else is prose.
export function toMarkdown(cells: Cell[]): string {
  const parts = cells.map(cell => cell.kind === "sql"
    ? `\`\`\`sql${cell.connectionId ? ` conn=${cell.connectionId}` : ""}\n${cell.text.replace(/\n+$/, "")}\n\`\`\``
    : cell.text.trim());

  return parts.filter(part => part.length > 0).join("\n\n") + "\n";
}

/// The other direction. A fenced sql block becomes a SQL cell, everything between blocks becomes
/// one note cell — so a document written by hand opens as a notebook without ceremony.
export function fromMarkdown(text: string): Cell[] {
  const cells: Cell[] = [];
  const lines = text.split(/\r?\n/);

  let prose: string[] = [];
  let fence: { connectionId?: string; lines: string[] } | null = null;

  const flushProse = () => {
    const joined = prose.join("\n").trim();
    if (joined.length > 0) cells.push({ id: newId(), kind: "note", text: joined });
    prose = [];
  };

  for (const line of lines) {
    const opening: RegExpExecArray | null =
      fence === null ? /^```sql(?:\s+conn=(\S+))?\s*$/.exec(line) : null;

    if (opening) {
      flushProse();
      fence = { connectionId: opening[1], lines: [] };
      continue;
    }

    if (fence !== null) {
      if (/^```\s*$/.test(line)) {
        cells.push({
          id: newId(), kind: "sql", text: fence.lines.join("\n").trim(),
          connectionId: fence.connectionId,
        });
        fence = null;
        continue;
      }

      fence.lines.push(line);
      continue;
    }

    prose.push(line);
  }

  // An unclosed fence is still SQL somebody wrote; losing it would be worse than keeping it.
  if (fence !== null)
    cells.push({
      id: newId(), kind: "sql", text: fence.lines.join("\n").trim(),
      connectionId: fence.connectionId,
    });

  flushProse();
  return cells;
}

/// The workspace key a notebook is stored under.
export const notebookKey = (id: string) => `notebook:${id}`;

export const notebookIndexKey = "notebook-index";
