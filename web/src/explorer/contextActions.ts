export type ExplorerAction =
  | "select" | "refresh"
  | "open-data" | "new-query" | "show-ddl" | "copy-name" | "copy-link" | "structure"
  | "design" | "new-table" | "rename"
  | "manage-indexes" | "add-index"
  | "export" | "import" | "copy-table"
  | "new-database" | "drop-database" | "properties"
  | "script-insert" | "script-update" | "script-delete" | "script-truncate" | "script-drop"
  | "script-select-column" | "script-drop-column"
  | "script-drop-index" | "script-reindex"
  | "script-drop-constraint"
  | "script-execute" | "script-refresh-matview" | "script-refresh-matview-live"
  | "grant-schema" | "archive-table" | "dev-subset"
  // Object storage: the object itself rather than the rows in it.
  | "download-object" | "save-object" | "upload-object" | "delete-object" | "query-as-table"
  | "copy-uri" | "import-object" | "download-prefix" | "save-prefix";

export interface ContextItem {
  action: ExplorerAction;
  label: string;
  /// Drawn in red and, where it matters, separated from the harmless items above it.
  danger?: boolean;
  /// Starts a new block in the menu.
  divider?: boolean;
}

/// What a driver said it can do. Only the flags the menu cares about are read.
export interface MenuCapabilities {
  ddl?: boolean;
  multiDatabase?: boolean;
  fullTextIndexes?: boolean;
  /// Whether a database or folder is itself a page of rows — a key space is, and its inventory of
  /// keys is the table worth opening.
  browseContainers?: boolean;
}

const TABLE: ContextItem[] = [
  { action: "open-data", label: "Open data" },
  { action: "new-query", label: "New query (SELECT *)" },
  { action: "structure", label: "Show structure" },
  { action: "show-ddl", label: "Show DDL" },
  { action: "design", label: "Design table…", divider: true },
  { action: "manage-indexes", label: "Indexes…" },
  { action: "rename", label: "Rename…" },
  { action: "script-insert", label: "Script: INSERT", divider: true },
  { action: "script-update", label: "Script: UPDATE" },
  { action: "script-delete", label: "Script: DELETE" },
  { action: "export", label: "Export…", divider: true },
  { action: "archive-table", label: "Keep as archive…" },
  { action: "dev-subset", label: "Development subset…" },
  { action: "import", label: "Import into this table…" },
  { action: "copy-table", label: "Copy to another connection…" },
  { action: "copy-name", label: "Copy name", divider: true },
  { action: "copy-link", label: "Copy link" },
  { action: "script-truncate", label: "Script: TRUNCATE", danger: true, divider: true },
  { action: "script-drop", label: "Script: DROP", danger: true },
];

const VIEW: ContextItem[] = [
  { action: "open-data", label: "Open data" },
  { action: "new-query", label: "New query (SELECT *)" },
  { action: "structure", label: "Show structure" },
  { action: "show-ddl", label: "Show DDL" },
  { action: "export", label: "Export…", divider: true },
  { action: "copy-name", label: "Copy name" },
  { action: "copy-link", label: "Copy link" },
  { action: "script-drop", label: "Script: DROP", danger: true, divider: true },
];

const ROUTINE: ContextItem[] = [
  { action: "show-ddl", label: "Show source" },
  { action: "script-execute", label: "Script: execute" },
  { action: "copy-name", label: "Copy name" },
  { action: "script-drop", label: "Script: DROP", danger: true, divider: true },
];

const CONTAINER: ContextItem[] = [
  { action: "new-table", label: "New table…" },
  { action: "refresh", label: "Refresh" },
];

const STORAGE_OBJECT: ContextItem[] = [
  { action: "open-data", label: "Open data" },
  { action: "new-query", label: "New query (SELECT *)" },
  { action: "download-object", label: "Download" },
  { action: "save-object", label: "Save as…" },
  // A file in a bucket that should be a table in a database.
  { action: "import-object", label: "Import into a database…" },
  { action: "export", label: "Export…", divider: true },
  { action: "copy-uri", label: "Copy the URI" },
  { action: "copy-link", label: "Copy link" },
  { action: "delete-object", label: "Delete…", danger: true, divider: true },
];

const STORAGE_FOLDER: ContextItem[] = [
  // A folder is a table only once a pattern says which of its files belong together.
  { action: "query-as-table", label: "Query as table…" },
  { action: "upload-object", label: "Upload here…" },
  // A folder, taken with you: one zip rather than a click per file.
  { action: "download-prefix", label: "Download as zip" },
  { action: "save-prefix", label: "Save zip as…" },
  { action: "refresh", label: "Refresh" },
  { action: "copy-uri", label: "Copy the path", divider: true },
];

/// The menu for one node. Everything the engine cannot do is left out rather than shown broken.
export function actionsFor(kind: string, caps: MenuCapabilities = {}): ContextItem[] {
  const ddl = caps.ddl !== false;

  const keepWritable = (items: ContextItem[]) =>
    ddl ? items : items.filter(item => !WRITES.has(item.action));

  switch (kind) {
    case "Table":
      return keepWritable(TABLE);

    case "View":
      return keepWritable(VIEW);

    case "MaterializedView":
      return keepWritable([
        ...VIEW.slice(0, 4),
        { action: "script-refresh-matview", label: "Script: REFRESH" },
        { action: "script-refresh-matview-live", label: "Script: REFRESH CONCURRENTLY" },
        ...VIEW.slice(4),
      ]);

    case "Procedure":
    case "Function":
      return keepWritable(ROUTINE);

    case "Trigger":
      return keepWritable([
        { action: "show-ddl", label: "Show source" },
        { action: "copy-name", label: "Copy name" },
        { action: "script-drop", label: "Script: DROP", danger: true, divider: true },
      ]);

    case "Sequence":
      return keepWritable([
        { action: "show-ddl", label: "Show definition" },
        { action: "copy-name", label: "Copy name" },
        { action: "script-drop", label: "Script: DROP", danger: true, divider: true },
      ]);

    case "Column":
      return keepWritable([
        { action: "script-select-column", label: "New query with this column" },
        { action: "copy-name", label: "Copy name" },
        { action: "design", label: "Edit in the designer…", divider: true },
        { action: "add-index", label: "Add index on this column…" },
        { action: "script-drop-column", label: "Script: DROP COLUMN", danger: true, divider: true },
      ]);

    case "Index":
      return keepWritable([
        { action: "manage-indexes", label: "Indexes…" },
        { action: "copy-name", label: "Copy name" },
        { action: "script-reindex", label: "Script: rebuild", divider: true },
        { action: "script-drop-index", label: "Script: DROP INDEX", danger: true },
      ]);

    case "ForeignKey":
      return keepWritable([
        { action: "copy-name", label: "Copy name" },
        { action: "design", label: "Edit in the designer…" },
        { action: "script-drop-constraint", label: "Script: DROP CONSTRAINT", danger: true, divider: true },
      ]);

    case "Schema":
      return keepWritable([
        ...(caps.browseContainers ? [{ action: "open-data" as const, label: "Open data" }] : []),
        ...CONTAINER,
        { action: "export", label: "Export schema…" },
        { action: "new-query", label: "New query here" },
        { action: "grant-schema", label: "Privileges on everything here…" },
        { action: "properties", label: "Properties…", divider: true },
      ]);

    case "StorageObject":
      // Nothing here is DDL: a file has no schema to change, so the writes flag does not apply and
      // the delete is guarded on the server instead.
      return STORAGE_OBJECT;

    case "Container":
    case "Prefix":
      return STORAGE_FOLDER;

    case "TableFolder":
      return keepWritable(caps.browseContainers
        ? [{ action: "open-data", label: "Open data" }, ...CONTAINER]
        : CONTAINER);

    case "Database":
      return keepWritable([
        { action: "new-query", label: "New query" },
        { action: "refresh", label: "Refresh" },
        { action: "properties", label: "Properties…", divider: true },
        ...(caps.multiDatabase ? [
          { action: "new-database" as const, label: "New database…", divider: true },
          { action: "drop-database" as const, label: "Drop database…", danger: true },
        ] : []),
      ]);

    // Folders that only list things have nothing worth a menu beyond reloading them.
    case "ViewFolder":
    case "ProcedureFolder":
    case "FunctionFolder":
    case "TriggerFolder":
    case "SequenceFolder":
      return [{ action: "refresh", label: "Refresh" }];

    default:
      return [{ action: "refresh", label: "Refresh" }];
  }
}

/// Actions that change the database, or that only make sense where DDL is possible at all.
const WRITES = new Set<ExplorerAction>([
  "design", "new-table", "rename", "manage-indexes", "add-index", "import",
  "script-insert", "script-update", "script-delete", "script-truncate", "script-drop",
  "script-drop-column", "script-drop-index", "script-reindex", "script-drop-constraint",
  "script-refresh-matview", "script-refresh-matview-live", "grant-schema",
  "new-database", "drop-database",
]);

/// The menu for a connection's own row, which is not a schema node.
export const connectionActions = (caps: MenuCapabilities = {}): ContextItem[] => [
  { action: "new-query", label: "New query" },
  { action: "refresh", label: "Refresh" },
  { action: "properties", label: "Properties…", divider: true },
  ...(caps.multiDatabase ? [{ action: "new-database" as const, label: "New database…", divider: true }] : []),
];
