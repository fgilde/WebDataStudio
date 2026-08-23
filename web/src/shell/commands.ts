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
  refreshExplorer: () => void;
  goToObject: () => void;
  openDiagram: () => void;
  openHealth: () => void;
  openAdmin: () => void;
  openCompare: () => void;
  openHistory: () => void;
  openSavedQueries: () => void;
  saveCurrentQuery: () => void;
  exportResult: () => void;
  openSnippets: () => void;
  showExplorer: () => void;
  openInBuilder: () => void;
  openNotebook: () => void;
  openFederation: () => void;
  openPerspective: () => void;
  openArchives: () => void;
  switchTheme: () => void;
  saveLayout: () => void;
  resetLayout: () => void;
  copyLink: () => void;
  showShortcuts: () => void;
  openPreferences: () => void;
}

/// One registry, read by both the palette and the shortcut help. A command can never appear in
/// one and be missing from the other, which is the usual way those two drift apart.
export function buildCommands(context: CommandContext): Command[] {
  return [
    { id: "query.new", label: "New query tab", group: "Query", shortcut: "Ctrl+N", run: context.newQuery },
    { id: "query.run", label: "Run statement", group: "Query", shortcut: "F5", run: context.runCurrent },
    { id: "query.cancel", label: "Cancel running query", group: "Query", shortcut: "Ctrl+Shift+C", run: context.cancelCurrent },
    { id: "query.format", label: "Format SQL", group: "Query", shortcut: "Ctrl+Shift+F", run: context.formatCurrent },
    { id: "query.save", label: "Save query", group: "Query", shortcut: "Ctrl+S", run: context.saveCurrentQuery },
    { id: "query.saved", label: "Open saved queries", group: "Query", run: context.openSavedQueries },
    { id: "query.history", label: "Open history", group: "Query", shortcut: "Ctrl+H", run: context.openHistory },
    { id: "query.snippets", label: "Manage snippets", group: "Query", run: context.openSnippets },
    // Only does something for a statement the builder produced, which is where the model lives.
    { id: "query.toBuilder", label: "Open this query in the builder", group: "Query", run: context.openInBuilder },

    { id: "connection.manage", label: "Open connection manager", group: "Connections", run: context.openConnections },
    { id: "connection.add", label: "Add connection", group: "Connections", run: context.addConnection },
    { id: "explorer.refresh", label: "Refresh explorer", group: "Connections", shortcut: "F6", run: context.refreshExplorer },
    { id: "explorer.goto", label: "Go to object", group: "Connections", shortcut: "Ctrl+Shift+O", run: context.goToObject },

    { id: "tool.diagram", label: "Open ER diagram", group: "Tools", shortcut: "Ctrl+D", run: context.openDiagram },
    { id: "tool.perspective", label: "Open perspective — a row and everything related to it", group: "Tools", run: context.openPerspective },
    { id: "tool.archives", label: "Open archives — results kept as files", group: "Tools", run: context.openArchives },
    { id: "tool.health", label: "Open health report", group: "Tools", run: context.openHealth },
    { id: "tool.admin", label: "Open administration", group: "Tools", run: context.openAdmin },
    { id: "tool.compare", label: "Open compare", group: "Tools", run: context.openCompare },
    { id: "tool.notebook", label: "Open notebook", group: "Tools", run: context.openNotebook },
    { id: "tool.federate", label: "Join across connections", group: "Tools", run: context.openFederation },
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
