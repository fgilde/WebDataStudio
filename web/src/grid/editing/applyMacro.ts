export type Macro =
  | { kind: "set"; value: string }
  | { kind: "null" }
  | { kind: "trim" }
  | { kind: "upper" }
  | { kind: "lower" }
  | { kind: "replace"; find: string; with: string; regex: boolean }
  | { kind: "add"; amount: number }
  | { kind: "template"; pattern: string };

/// Transforms one cell value. Never throws: a macro that cannot apply to a value leaves it alone,
/// because a bulk edit must not turn one odd row into a failed run.
export function applyMacro(value: unknown, macro: Macro, rowIndex = 0): unknown {
  const text = value === null || value === undefined ? "" : String(value);

  switch (macro.kind) {
    case "set": return macro.value;
    case "null": return null;
    case "trim": return text.trim();
    case "upper": return text.toUpperCase();
    case "lower": return text.toLowerCase();

    case "replace": {
      if (!macro.regex) return text.split(macro.find).join(macro.with);
      try { return text.replace(new RegExp(macro.find, "g"), macro.with); }
      catch { return value; }
    }

    case "add": {
      const n = typeof value === "number" ? value : Number(text);
      return Number.isFinite(n) ? n + macro.amount : value;
    }

    case "template":
      return macro.pattern.replaceAll("{value}", text).replaceAll("{row}", String(rowIndex + 1));
  }
}

export function macroError(macro: Macro): string | null {
  if (macro.kind !== "replace" || !macro.regex) return null;
  try { new RegExp(macro.find); return null; }
  catch (e) { return e instanceof Error ? e.message : "invalid regular expression"; }
}
