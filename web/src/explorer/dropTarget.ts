/// What dropping a file on a node means.
///
/// The tree already knows what every node is, so a file dragged onto it can go where it obviously
/// belongs: into a bucket folder as an upload, into a table as rows, into a schema as a new table.
/// Everything else takes no files, and says so by not lighting up.
export type DropKind = "upload" | "import" | "new-table";

/// Which nodes accept a file, and as what.
export function dropKindFor(kind: string): DropKind | null {
  switch (kind) {
    // A folder in a bucket, or the bucket itself: the file lands there as it is.
    case "Container":
    case "Prefix":
      return "upload";

    // A table: the file's rows go into the table that already exists, through the import dialog's
    // column mapping.
    case "Table":
      return "import";

    // A schema or a table folder: there is no table yet, so the file becomes one.
    case "Schema":
    case "TableFolder":
    case "Database":
      return "new-table";

    default:
      return null;
  }
}

/// The files of a drag, or an empty list. A drag of text — a selection from another window — is not
/// a file drop and must not look like one.
export function filesOf(transfer: DataTransfer | null): File[] {
  if (!transfer) return [];

  const files = Array.from(transfer.files ?? []);
  if (files.length > 0) return files;

  // While a drag is still in progress the browser hides the file list, and only the item kinds are
  // readable. That is enough to decide whether to light a node up.
  return Array.from(transfer.items ?? [])
    .filter(item => item.kind === "file")
    .map(() => new File([], ""));
}

/// Whether this drag carries files at all — asked during dragover, where the names are hidden.
export const dragHasFiles = (transfer: DataTransfer | null): boolean =>
  Array.from(transfer?.items ?? []).some(item => item.kind === "file")
  || (transfer?.types ?? []).includes("Files");
