import { useSyncExternalStore } from "react";
import { loadWorkspaceItem, saveWorkspaceItem } from "../api";

export interface Preferences {
  /// Rows per page in the data tab.
  pageSize: number;
  /// Whether a query keeps its result with the history entry. Off by default: a snapshot is a copy
  /// of the data, and the workspace database is not where everybody wants that.
  historySnapshots: boolean;
  /// How many rows a snapshot keeps at most.
  snapshotRows: number;
  /// Command id to key combination, for the commands whose binding the user changed.
  shortcuts: Record<string, string>;
  /// Whether the studio reads a statement before running it and says what it noticed — an UPDATE
  /// with no WHERE, an accidental cross product. It only ever warns.
  inspectBeforeRun: boolean;
  /// Tell me when a query that took at least this many seconds is done, if I am looking at
  /// something else. 0 switches it off.
  notifyAfterSeconds: number;
  /// Which clock timestamps are shown on: "local", "utc", or an IANA name like "Europe/Berlin".
  /// Only what is shown — a value with no zone of its own is never converted.
  timeZone: string;
}

export const DEFAULT_PREFERENCES: Preferences = {
  pageSize: 200,
  historySnapshots: false,
  snapshotRows: 200,
  shortcuts: {},
  inspectBeforeRun: true,
  notifyAfterSeconds: 30,
  timeZone: "local",
};

const KEY = "preferences";

// One copy for the whole app, so a change reaches the data tab and the shortcut handler at once
// without threading a context through everything in between.
let current: Preferences = DEFAULT_PREFERENCES;
const listeners = new Set<() => void>();

const announce = () => listeners.forEach(listener => listener());

/// Read once at start-up. A workspace without preferences, or one that cannot be read, leaves the
/// defaults in place — this is nobody's reason to see an error.
export async function loadPreferences(): Promise<void> {
  try {
    const stored = await loadWorkspaceItem<Partial<Preferences>>(KEY);
    if (stored) {
      current = { ...DEFAULT_PREFERENCES, ...stored, shortcuts: stored.shortcuts ?? {} };
      announce();
    }
  } catch { /* the defaults are a fine answer */ }
}

export async function savePreferences(next: Partial<Preferences>): Promise<void> {
  current = { ...current, ...next };
  announce();
  await saveWorkspaceItem(KEY, current);
}

export const preferences = (): Preferences => current;

export function usePreferences(): Preferences {
  return useSyncExternalStore(
    listener => { listeners.add(listener); return () => listeners.delete(listener); },
    () => current,
    () => current,
  );
}

/// "Ctrl+Shift+K" for a keyboard event, in the spelling the command list already uses. Modifier
/// order is fixed so a recorded binding and a typed one compare equal.
export function comboOf(event: KeyboardEvent): string {
  const parts: string[] = [];
  if (event.ctrlKey || event.metaKey) parts.push("Ctrl");
  if (event.altKey) parts.push("Alt");
  if (event.shiftKey) parts.push("Shift");

  const key = event.key.length === 1 ? event.key.toUpperCase() : event.key;
  // A bare modifier is half a binding, not a binding.
  if (["Control", "Meta", "Alt", "Shift"].includes(event.key)) return "";

  parts.push(key);
  return parts.join("+");
}
