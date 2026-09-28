/// Saved-query folders are paths, "reports/monthly/finance": the column the server has always had,
/// with a slash for the nesting. A folder with nothing in it yet is kept in a workspace item of its
/// own, because the only other place a folder exists is on the queries inside it.

export const EMPTY_FOLDERS_KEY = "saved-query-folders";

/// Trims every segment and drops empty ones, so "a//b/ " and "a/b" are one folder. Null for none.
export function normalizeFolder(path: string | null | undefined): string | null {
  const segments = (path ?? "").split("/").map(part => part.trim()).filter(Boolean);
  return segments.length ? segments.join("/") : null;
}

export const parentOf = (path: string): string | null =>
  path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : null;

export const leafOf = (path: string): string => path.slice(path.lastIndexOf("/") + 1);

export const depthOf = (path: string): number => path.split("/").length - 1;

export const isWithin = (path: string | null, folder: string): boolean =>
  path === folder || (path?.startsWith(folder + "/") ?? false);

/// Every folder there is, with each one's parents, sorted so a parent comes before its children.
export function allFolders(paths: Iterable<string | null>): string[] {
  const found = new Set<string>();
  for (const raw of paths) {
    let path = normalizeFolder(raw);
    while (path) { found.add(path); path = parentOf(path); }
  }
  return [...found].sort((a, b) => a.localeCompare(b));
}

/// The path after the folder `from` became `to`: its own contents and every subfolder move along.
/// Anything outside `from` stays where it is.
export function moveFolder(path: string | null, from: string, to: string | null): string | null {
  if (!path || !isWithin(path, from)) return path;
  return normalizeFolder(`${to ?? ""}/${path.slice(from.length)}`);
}
