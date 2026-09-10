/// The dashboard model, the same one the server keeps. Names and cases match its JSON exactly, so
/// a dashboard travels as it is rather than through a mapping layer nobody would keep in step.

export type WidgetType =
  | "Stat" | "Gauge" | "Bar" | "StackedBar" | "Pie" | "Treemap"
  | "Line" | "Area" | "StackedArea" | "Sparkline"
  | "Table" | "List" | "Heatmap" | "GeoMap" | "Sankey" | "Text" | "Row";

export type SourceKind = "Sql" | "Path" | "Federated";
export type VariableKind = "Query" | "Custom" | "Constant" | "Interval";
export type ThresholdLevel = "good" | "warning" | "serious" | "critical";

export interface WidgetPosition { x: number; y: number; w: number; h: number }
export interface FederatedSource { connectionId: string; sql: string; alias: string }

export interface WidgetSource {
  kind: SourceKind;
  connectionId?: string | null;
  sql?: string | null;
  sources?: FederatedSource[];
  maxRowsPerSource?: number | null;
}

/// Which column plays which role. Apart from the options on purpose — see the spec: one bag of
/// everything is what makes fifteen widget types unmaintainable.
export interface WidgetMapping {
  time?: string | null;
  category?: string | null;
  series?: string | null;
  values?: string[];
  latitude?: string | null;
  longitude?: string | null;
  from?: string | null;
  to?: string | null;
  weight?: string | null;
}

export interface WidgetThreshold { value: number; level: ThresholdLevel }

export interface WidgetOptions {
  unit?: string | null;
  decimals?: number | null;
  min?: number | null;
  max?: number | null;
  thresholds?: WidgetThreshold[];
  legend?: boolean;
  stacked?: boolean;
  orientation?: string | null;
  colorMode?: string | null;
  ramp?: string | null;
  markdown?: string | null;
  columns?: string[];
  limit?: number | null;
  /// The panel as Grafana wrote it. Never read by anything that draws; it exists so an export puts
  /// back what came in.
  grafana?: unknown;
}

export interface Widget {
  id: string;
  type: WidgetType;
  title: string;
  description?: string | null;
  position: WidgetPosition;
  source: WidgetSource;
  mapping: WidgetMapping;
  options: WidgetOptions;
}

export interface DashboardLayout { columns: number; rowHeight: number }
export interface DashboardTimeRange { from: string; to: string; column?: string | null }

export interface DashboardVariable {
  name: string;
  kind: VariableKind;
  label?: string | null;
  connectionId?: string | null;
  sql?: string | null;
  values?: string[];
  multi?: boolean;
  includeAll?: boolean;
  default?: string | null;
}

export interface Dashboard {
  id: string;
  name: string;
  widgets: Widget[];
  refreshSeconds: number;
  updatedAt: string;
  layout?: DashboardLayout | null;
  timeRange?: DashboardTimeRange | null;
  variables?: DashboardVariable[];
  tags?: string[];
  /// True for one the deployment ships: shown, and not editable here.
  fromFile?: boolean;
}

export const COLUMNS = 24;

/// What each type is called in the palette, and what it is for. The order is the palette's order:
/// the headline forms first, then the shapes, then the ones that carry rows.
export const WIDGET_TYPES: {
  type: WidgetType; label: string; hint: string; group: string;
}[] = [
  { type: "Stat", label: "Stat", hint: "one headline number", group: "Numbers" },
  { type: "Gauge", label: "Gauge", hint: "a number against a range", group: "Numbers" },
  { type: "Sparkline", label: "Sparkline", hint: "a shape, small", group: "Numbers" },
  { type: "Bar", label: "Bar", hint: "magnitude per category", group: "Shapes" },
  { type: "StackedBar", label: "Stacked bar", hint: "parts per category", group: "Shapes" },
  { type: "Line", label: "Line", hint: "change over time", group: "Shapes" },
  { type: "Area", label: "Area", hint: "change over time, filled", group: "Shapes" },
  { type: "StackedArea", label: "Stacked area", hint: "parts over time", group: "Shapes" },
  { type: "Pie", label: "Pie", hint: "part of a whole, few parts", group: "Shapes" },
  { type: "Treemap", label: "Treemap", hint: "part of a whole, many parts", group: "Shapes" },
  { type: "Heatmap", label: "Heatmap", hint: "density across two dimensions", group: "Shapes" },
  { type: "Sankey", label: "Sankey", hint: "flow from one thing to another", group: "Shapes" },
  { type: "GeoMap", label: "Map", hint: "where the rows are", group: "Shapes" },
  { type: "Table", label: "Table", hint: "rows as rows", group: "Rows" },
  { type: "List", label: "List", hint: "a label and a value, for a top ten", group: "Rows" },
  { type: "Text", label: "Text", hint: "Markdown, with the variables filled in", group: "Rows" },
  { type: "Row", label: "Section", hint: "a band that collapses", group: "Rows" },
];

/// Types that draw a shape rather than rows or prose. The frame gives these the table-view toggle
/// and the chart mount; the others render themselves.
export const CHART_TYPES: WidgetType[] = [
  "Bar", "StackedBar", "Pie", "Treemap", "Line", "Area", "StackedArea", "Sparkline", "Heatmap",
  "Sankey", "Gauge",
];

export const isChart = (type: WidgetType) => CHART_TYPES.includes(type);

const id = () => Math.random().toString(36).slice(2, 10);

export const emptyWidget = (type: WidgetType, at: Partial<WidgetPosition> = {}): Widget => ({
  id: id(),
  type,
  title: WIDGET_TYPES.find(one => one.type === type)?.label ?? "Widget",
  position: {
    x: at.x ?? 0,
    y: at.y ?? 0,
    w: at.w ?? (type === "Row" ? COLUMNS : type === "Stat" ? 6 : 12),
    h: at.h ?? (type === "Row" ? 1 : type === "Stat" ? 4 : 8),
  },
  source: { kind: "Sql", connectionId: null, sql: "" },
  mapping: {},
  options: { legend: true, thresholds: [] },
});

export const emptyDashboard = (name = "New dashboard"): Dashboard => ({
  id: "",
  name,
  widgets: [],
  refreshSeconds: 0,
  updatedAt: new Date().toISOString(),
  layout: { columns: COLUMNS, rowHeight: 40 },
  timeRange: { from: "now-24h", to: "now" },
  variables: [],
  tags: [],
});

/// A dashboard as it arrives, whatever shape the server had it in. The server migrates the stored
/// ones, so this only has to cope with the fields an older answer leaves out.
export const readDashboard = (dto: Partial<Dashboard> & { tiles?: unknown }): Dashboard => ({
  ...emptyDashboard(dto.name ?? "untitled"),
  ...dto,
  widgets: (dto.widgets ?? []).map(widget => ({
    ...widget,
    mapping: widget.mapping ?? {},
    options: { legend: true, thresholds: [], ...widget.options },
    source: widget.source ?? { kind: "Sql" },
  })),
  layout: dto.layout ?? { columns: COLUMNS, rowHeight: 40 },
  timeRange: dto.timeRange ?? { from: "now-24h", to: "now" },
  variables: dto.variables ?? [],
});

/// The ranges the picker offers. Relative, because a dashboard on a wall means "the last day"
/// rather than a Tuesday in September.
export const RANGES: { label: string; from: string; to: string }[] = [
  { label: "Last 15 minutes", from: "now-15m", to: "now" },
  { label: "Last hour", from: "now-1h", to: "now" },
  { label: "Last 6 hours", from: "now-6h", to: "now" },
  { label: "Last 24 hours", from: "now-24h", to: "now" },
  { label: "Last 7 days", from: "now-7d", to: "now" },
  { label: "Last 30 days", from: "now-30d", to: "now" },
  { label: "Last 90 days", from: "now-90d", to: "now" },
  { label: "This year", from: "now/y", to: "now" },
  { label: "Everything", from: "now-10y", to: "now" },
];

export const rangeLabel = (range: DashboardTimeRange | null | undefined) =>
  RANGES.find(one => one.from === range?.from && one.to === range?.to)?.label
  ?? `${range?.from ?? "now-24h"} → ${range?.to ?? "now"}`;
