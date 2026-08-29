/// How large the text in the editor is, and how that survives a reload.
///
/// One size for every editor in the studio rather than one per tab: zooming in on a query and then
/// opening the next one at 13px would be its own annoyance. Kept in the browser, because "how big
/// is the text on this screen" belongs to the screen and not to the workspace.

const KEY = "wds.editor.fontSize";

/// Small enough to fit a wall of SQL on a laptop, large enough to read across a room.
export const MIN_FONT_SIZE = 8;
export const MAX_FONT_SIZE = 40;
export const DEFAULT_FONT_SIZE = 13;

export const clampFontSize = (size: number) =>
  Math.min(MAX_FONT_SIZE, Math.max(MIN_FONT_SIZE, Math.round(size)));

export function readFontSize(): number {
  try {
    const stored = Number(localStorage.getItem(KEY));
    return Number.isFinite(stored) && stored > 0 ? clampFontSize(stored) : DEFAULT_FONT_SIZE;
  } catch {
    // A browser with site data switched off still gets an editor.
    return DEFAULT_FONT_SIZE;
  }
}

export function writeFontSize(size: number): number {
  const next = clampFontSize(size);

  try {
    localStorage.setItem(KEY, String(next));
  } catch { /* nothing to do about it, and nothing worth saying */ }

  return next;
}

/// Everything that should change the size, in one place so the editor only has to say what
/// happened rather than what it means.
///
/// Ctrl and the wheel is what every editor does; Ctrl with plus, minus and zero is what every
/// browser does. Returns the new size, or null when the event was not about zooming.
export function zoomFor(
  event: { ctrlKey: boolean; metaKey: boolean; key?: string; deltaY?: number },
  current: number,
): number | null {
  if (!event.ctrlKey && !event.metaKey) return null;

  if (typeof event.deltaY === "number" && event.deltaY !== 0)
    return clampFontSize(current + (event.deltaY < 0 ? 1 : -1));

  switch (event.key) {
    // The unshifted keys and the ones a keyboard actually produces: "+" needs shift on most
    // layouts, and the numeric pad sends "Add" and "Subtract" on none of them but is worth having.
    case "+": case "=": case "Add": return clampFontSize(current + 1);
    case "-": case "_": case "Subtract": return clampFontSize(current - 1);
    case "0": return DEFAULT_FONT_SIZE;
    default: return null;
  }
}
