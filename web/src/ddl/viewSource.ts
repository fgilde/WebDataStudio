export type EditableKind = "view" | "procedure" | "function" | "trigger";

/// A view opens as its SELECT rather than as the whole CREATE: that is the part somebody edits, and
/// the engine's own text has the name and the options wrapped around it. Everything up to the first
/// `AS` is dropped; a definition that does not look like that is left whole, because a wrong guess
/// here would silently lose half of somebody's SQL.
export function source(create: string, kind: EditableKind): string {
  if (kind !== "view") return create.trim();

  const match = /^\s*CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:MATERIALIZED\s+)?VIEW\b[\s\S]*?\bAS\b\s*/i
    .exec(create);

  return match ? create.slice(match[0].length).trim() : create.trim();
}
