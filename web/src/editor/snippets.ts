export interface Snippet { prefix: string; label: string; body: string; description: string }

/// `${1:name}` is Monaco's tab-stop syntax; the numbers are the tab order, the text after the
/// colon is the placeholder.
export const BUILT_IN: Snippet[] = [
  {
    prefix: "sel", label: "SELECT … WHERE", description: "A filtered select",
    body: "SELECT ${1:*}\n  FROM ${2:table}\n WHERE ${3:condition};",
  },
  {
    prefix: "ins", label: "INSERT", description: "An insert with a column list",
    body: "INSERT INTO ${1:table} (${2:columns})\nVALUES (${3:values});",
  },
  {
    prefix: "upd", label: "UPDATE", description: "An update with a where clause",
    body: "UPDATE ${1:table}\n   SET ${2:column} = ${3:value}\n WHERE ${4:condition};",
  },
  {
    prefix: "del", label: "DELETE", description: "A delete with a where clause",
    body: "DELETE FROM ${1:table}\n WHERE ${2:condition};",
  },
  {
    prefix: "join", label: "JOIN", description: "A select over two joined tables",
    body: "SELECT ${1:a}.*, ${2:b}.*\n  FROM ${3:left} ${1:a}\n  JOIN ${4:right} ${2:b}\n    ON ${1:a}.${5:id} = ${2:b}.${6:ref};",
  },
  {
    prefix: "cte", label: "WITH …", description: "A common table expression",
    body: "WITH ${1:name} AS (\n    ${2:SELECT 1}\n)\nSELECT * FROM ${1:name};",
  },
  {
    prefix: "idx", label: "CREATE INDEX", description: "An index on one or more columns",
    body: "CREATE INDEX ${1:ix_name}\n    ON ${2:table} (${3:columns});",
  },
  {
    prefix: "cnt", label: "GROUP BY count", description: "Counts per value of a column",
    body: "SELECT ${1:column}, count(*) AS n\n  FROM ${2:table}\n GROUP BY ${1:column}\n ORDER BY n DESC;",
  },
];

const KEY = "snippets";

export interface SnippetStore { load(): Promise<Snippet[]>; save(list: Snippet[]): Promise<void> }

/// User snippets live in the workspace, so they follow the user across browsers rather than
/// sitting in one machine's local storage.
export function workspaceSnippets(
  read: (key: string) => Promise<unknown>,
  write: (key: string, value: unknown) => Promise<void>): SnippetStore {
  return {
    async load() {
      const stored = await read(KEY).catch(() => null);
      return Array.isArray(stored) ? (stored as Snippet[]) : [];
    },
    save: list => write(KEY, list),
  };
}

export const allSnippets = (user: Snippet[]): Snippet[] => {
  // A user snippet with a built-in's prefix wins: overriding one is a deliberate act.
  const overridden = new Set(user.map(s => s.prefix));
  return [...user, ...BUILT_IN.filter(s => !overridden.has(s.prefix))];
};
