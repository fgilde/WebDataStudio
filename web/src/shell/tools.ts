/// Every tool the studio can open, in one list.
///
/// Three surfaces used to carry their own copy of this: the explorer's icon row, the command palette
/// and the keyboard help. They drifted — Find data, the query builder and the Redis browser were
/// buttons the palette had never heard of. So the list lives here, and each surface renders it.
///
/// `dock` says how a tool is opened: a `tool` gets one panel per connection, a `panel` is a single
/// panel that is focused rather than duplicated, and a `route` is a page of its own — for the one
/// surface that is deliberately not a dock panel, because the person using it is not here to write
/// SQL.
export interface ToolDefinition {
  /// The command id, which is also the palette's and the keyboard help's key for it.
  id: string;
  /// The dockview component for a tool, the panel id for a panel, or the path for a route.
  component: string;
  dock: "tool" | "panel" | "route";
  /// The dock tab's title.
  title: string;
  /// What a menu or the palette shows.
  label: string;
  shortcut?: string;
  /// Nothing to open without a connection: the entry is offered but disabled.
  requiresConnection?: boolean;
  /// Only for this engine — a key browser on PostgreSQL would be a promise the button cannot keep.
  engine?: string;
  /// The server refuses administration for anybody else, so offering it would be the same.
  adminOnly?: boolean;
  /// Which tab the panel opens on, where it has tabs.
  tab?: string;
}

export const TOOLS: ToolDefinition[] = [
  {
    id: "tool.datasearch", component: "datasearch", dock: "tool", title: "Find data",
    label: "Find a value in any table", requiresConnection: true,
  },
  {
    id: "tool.diagram", component: "diagram", dock: "tool", title: "Diagram",
    label: "ER diagram", shortcut: "Ctrl+D", requiresConnection: true,
  },
  {
    id: "tool.builder", component: "builder", dock: "tool", title: "Builder",
    label: "Query builder", requiresConnection: true,
  },
  {
    id: "tool.notebook", component: "notebook", dock: "tool", title: "Notebook",
    label: "Notebook — SQL, prose and results in one document", requiresConnection: true,
  },
  {
    id: "tool.perspective", component: "perspective", dock: "tool", title: "Perspective",
    label: "Perspective — a row and everything related to it", requiresConnection: true,
  },
  {
    id: "tool.compare", component: "compare", dock: "tool", title: "Compare",
    label: "Compare two connections", requiresConnection: true,
  },
  {
    id: "tool.federate", component: "federate", dock: "tool", title: "Federated",
    label: "Join across connections", requiresConnection: true,
  },
  {
    id: "tool.archive", component: "archive", dock: "tool", title: "Archives",
    label: "Archives — results kept as files", requiresConnection: true,
  },
  {
    id: "tool.dashboard", component: "dashboard", dock: "tool", title: "Dashboard",
    label: "Dashboard", requiresConnection: false,
  },
  {
    id: "tool.redis", component: "redis", dock: "tool", title: "Redis",
    label: "Redis key browser", requiresConnection: true, engine: "redis",
  },
  {
    id: "tool.admin", component: "admin", dock: "tool", title: "Admin",
    label: "Administration", requiresConnection: true, adminOnly: true,
  },
  {
    id: "tool.jobs", component: "admin", dock: "tool", title: "Admin",
    label: "Scheduled jobs — Agent, pg_cron, events", requiresConnection: true, adminOnly: true,
    tab: "jobs",
  },
  {
    id: "tool.capture", component: "admin", dock: "tool", title: "Admin",
    label: "Capture — what runs in the next minute", requiresConnection: true, adminOnly: true,
    tab: "capture",
  },
  {
    id: "tool.health", component: "health", dock: "panel", title: "Health",
    label: "Health report",
  },
  {
    id: "tool.reports", component: "/report", dock: "route", title: "Reports",
    label: "Reports — a saved query as a form",
  },
  {
    id: "tool.history", component: "history", dock: "panel", title: "History",
    label: "Query history", shortcut: "Ctrl+H",
  },
  {
    id: "tool.saved", component: "saved", dock: "panel", title: "Saved",
    label: "Saved queries",
  },
];

/// What a surface may show: an admin-only tool is left out for everybody else rather than shown
/// disabled, and an engine-specific one only appears on that engine.
export function visibleTools(options: { admin: boolean; engine?: string }): ToolDefinition[] {
  return TOOLS.filter(tool =>
    (!tool.adminOnly || options.admin)
    && (tool.engine === undefined || tool.engine === options.engine));
}

/// The components the dock has to provide. Typing the dock's map against this is what makes a tool
/// without a panel a compile error rather than a click that does nothing.
export type ToolComponent = "datasearch" | "diagram" | "builder" | "notebook" | "perspective"
  | "compare" | "federate" | "archive" | "redis" | "admin" | "health" | "history" | "saved"
  | "dashboard";
