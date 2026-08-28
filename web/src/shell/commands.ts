import { visibleTools, type ToolDefinition } from "./tools";

export interface Command {
  id: string;
  label: string;
  group: string;
  shortcut?: string;
  run: () => void;
  disabled?: boolean;
}

export interface CommandContext {
  newQuery: () => void;
  runCurrent: () => void;
  cancelCurrent: () => void;
  formatCurrent: () => void;
  openConnections: () => void;
  addConnection: () => void;
  addBucket: () => void;
  refreshExplorer: () => void;
  goToObject: () => void;
  /// Opens one of the tools in `tools.ts`. One entry point rather than one callback per tool: the
  /// list of tools grew four times and the callbacks did not keep up.
  openTool: (tool: ToolDefinition) => void;
  saveCurrentQuery: () => void;
  exportResult: () => void;
  openSnippets: () => void;
  showExplorer: () => void;
  openInBuilder: () => void;
  switchTheme: () => void;
  saveLayout: () => void;
  resetLayout: () => void;
  copyLink: () => void;
  showShortcuts: () => void;
  openPreferences: () => void;
  /// What is selected right now, so a tool that needs a connection is offered as disabled rather
  /// than as a click that does nothing.
  activeConnection?: string;
  engine?: string;
  admin?: boolean;
}

/// One registry, read by the palette, the keyboard help and the header's tools menu. A command can
/// never appear in one and be missing from the others, which is the usual way those drift apart.
export function buildCommands(context: CommandContext): Command[] {
  const tools = visibleTools({ admin: context.admin ?? true, engine: context.engine })
    .map(tool => ({
      id: tool.id,
      label: tool.label,
      group: "Tools",
      shortcut: tool.shortcut,
      disabled: tool.requiresConnection === true && !context.activeConnection,
      run: () => context.openTool(tool),
    }));

  return [
    { id: "query.new", label: "New query tab", group: "Query", shortcut: "Ctrl+N", run: context.newQuery },
    { id: "query.run", label: "Run statement", group: "Query", shortcut: "F5", run: context.runCurrent },
    { id: "query.cancel", label: "Cancel running query", group: "Query", shortcut: "Ctrl+Shift+C", run: context.cancelCurrent },
    { id: "query.format", label: "Format SQL", group: "Query", shortcut: "Ctrl+Shift+F", run: context.formatCurrent },
    { id: "query.save", label: "Save query", group: "Query", shortcut: "Ctrl+S", run: context.saveCurrentQuery },
    { id: "query.snippets", label: "Manage snippets", group: "Query", run: context.openSnippets },
    // Only does something for a statement the builder produced, which is where the model lives.
    { id: "query.toBuilder", label: "Open this query in the builder", group: "Query", run: context.openInBuilder },

    { id: "connection.manage", label: "Open connection manager", group: "Connections", run: context.openConnections },
    { id: "connection.add", label: "Add connection", group: "Connections", run: context.addConnection },
    // A bucket is a connection whose form asks for the pieces instead of a URL.
    { id: "connection.bucket", label: "Add a bucket — S3, Azure Blob, Google Cloud, a folder", group: "Connections", run: context.addBucket },
    { id: "explorer.refresh", label: "Refresh explorer", group: "Connections", shortcut: "F6", run: context.refreshExplorer },
    { id: "explorer.goto", label: "Go to object", group: "Connections", shortcut: "Ctrl+Shift+O", run: context.goToObject },

    ...tools,
    { id: "result.export", label: "Export result", group: "Tools", shortcut: "Ctrl+E", run: context.exportResult },

    { id: "view.explorer", label: "Show explorer", group: "View", shortcut: "Ctrl+B", run: context.showExplorer },
    { id: "view.theme", label: "Switch theme", group: "View", shortcut: "Ctrl+T", run: context.switchTheme },
    { id: "view.saveLayout", label: "Layout presets — save, or apply 1…9", group: "View", shortcut: "Ctrl+L", run: context.saveLayout },
    // Reachable even with every panel closed: this is the way back from a broken layout.
    { id: "view.resetLayout", label: "Reset layout", group: "View", shortcut: "Ctrl+L then 0", run: context.resetLayout },
    { id: "view.copyLink", label: "Copy link to this object", group: "View", run: context.copyLink },
    { id: "view.shortcuts", label: "Keyboard shortcuts", group: "View", shortcut: "?", run: context.showShortcuts },
    { id: "view.preferences", label: "Preferences", group: "View", shortcut: "Ctrl+,", run: context.openPreferences },
  ];
}

/// Matches on the label and the group so "diagram" and "tools" both find the diagram command.
export const filterCommands = (commands: Command[], search: string): Command[] => {
  const needle = search.trim().toLowerCase();
  if (!needle) return commands;

  return commands.filter(c =>
    c.label.toLowerCase().includes(needle) ||
    c.group.toLowerCase().includes(needle) ||
    c.id.toLowerCase().includes(needle));
};
