import { useSyncExternalStore } from "react";
import type { Command } from "./commands";

/// The header lives outside the dock — it wraps the routes — so the two cannot talk through props.
/// They talked through `document.dispatchEvent(new CustomEvent("wds:layouts"))` instead, which works
/// and says nothing about what the payload is. This is the same idea with the names and the payloads
/// written down once.
export interface ShellEvents {
  /// Open the layout presets dialog.
  layouts: void;
  /// Open the command palette.
  palette: void;
  /// Run a command the dock owns, by its id.
  command: string;
  /// Put SQL into the editor — the assistant's chat dock does this.
  "use-sql": string;
  /// Next theme.
  "cycle-theme": void;
}

const prefix = "wds:";

export function emit<K extends keyof ShellEvents>(
  name: K, detail?: ShellEvents[K] extends void ? undefined : ShellEvents[K]): void {
  document.dispatchEvent(new CustomEvent(prefix + name, { detail }));
}

/// Subscribes and returns the unsubscribe, which is what a React effect wants to give back.
export function onShell<K extends keyof ShellEvents>(
  name: K, handler: (detail: ShellEvents[K]) => void): () => void {
  const listener = (event: Event) => handler((event as CustomEvent).detail);

  document.addEventListener(prefix + name, listener);
  return () => document.removeEventListener(prefix + name, listener);
}

/// What the header needs to render, published by the dock.
///
/// The header cannot know whether a connection is selected or which engine it is, and a menu that
/// offers a Redis browser on PostgreSQL — or an entry that silently does nothing because no
/// connection is chosen — is worse than no menu.
export interface ShellSnapshot {
  activeConnection: string;
  engine: string;
  admin: boolean;
  /// The dock's own command list, so the header renders the same entries as the palette.
  commands: Command[];
}

let snapshot: ShellSnapshot = { activeConnection: "", engine: "", admin: false, commands: [] };
const listeners = new Set<() => void>();

export function publishShell(next: ShellSnapshot): void {
  snapshot = next;
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export const useShellSnapshot = (): ShellSnapshot =>
  useSyncExternalStore(subscribe, () => snapshot, () => snapshot);

/// For tests: back to nothing published.
export function resetShell(): void {
  publishShell({ activeConnection: "", engine: "", admin: false, commands: [] });
}
