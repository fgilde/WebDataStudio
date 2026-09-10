// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import type { Dashboard } from "./model";

window.matchMedia ??= ((query: string) => ({
  matches: false, media: query, onchange: null,
  addListener: () => {}, removeListener: () => {},
  addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
})) as typeof window.matchMedia;

globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

// Mantine's dropdown scrolls the highlighted option into view; jsdom has no scrolling.
Element.prototype.scrollIntoView ??= () => {};

const listDashboards = vi.fn();
const saveDashboard = vi.fn();
const deleteDashboard = vi.fn();
const listConnections = vi.fn();
const importDashboard = vi.fn();
const exportDashboardToGrafana = vi.fn();
const runQuery = vi.fn();

vi.mock("../api", () => ({
  listDashboards: (...args: unknown[]) => listDashboards(...args),
  saveDashboard: (...args: unknown[]) => saveDashboard(...args),
  deleteDashboard: (...args: unknown[]) => deleteDashboard(...args),
  listConnections: (...args: unknown[]) => listConnections(...args),
  importDashboard: (...args: unknown[]) => importDashboard(...args),
  exportDashboardToGrafana: (...args: unknown[]) => exportDashboardToGrafana(...args),
}));

vi.mock("../query/runQuery", () => ({ runQuery: (...args: unknown[]) => runQuery(...args) }));
vi.mock("../federate/runFederation", () => ({ runFederation: vi.fn(async () => {}) }));

// GridStack moves DOM nodes and measures them, neither of which jsdom does; the canvas has its own
// check in scripts/smoke-dashboard.mjs, where there is a browser. Here it is a plain list, so the
// test is about the panel.
vi.mock("./Canvas", () => ({
  Canvas: ({ dashboard, children }: {
    dashboard: Dashboard; children: (widget: unknown) => unknown;
  }) => <div data-testid="canvas">{dashboard.widgets.map(widget => (
    <div key={widget.id}>{children(widget) as never}</div>
  ))}</div>,
}));

// A canvas element is what ECharts wants, and jsdom has none.
vi.mock("./charts/EChart", () => ({
  EChart: () => <div data-testid="chart" />,
}));

const { DashboardPanel } = await import("./DashboardPanel");

/// A run that answers one row of one number, the way a "how many" widget is used.
const answers = (rows: unknown[][]) => (_request: unknown, onChunk: (chunk: unknown) => void) => {
  onChunk({ type: "columns", statement: 0, columns: [{ name: "n", dataType: "int", nullable: false }] });
  onChunk({ type: "rows", statement: 0, rows });
  onChunk({ type: "end", statement: 0, rowsAffected: 0, elapsedMs: 1, truncated: false });

  return { runId: Promise.resolve("r1"), done: Promise.resolve(), cancel: () => Promise.resolve() };
};

const dashboard = (over: Partial<Dashboard> = {}): Dashboard => ({
  id: "d1",
  name: "Morning",
  refreshSeconds: 0,
  updatedAt: "2026-09-10T08:00:00Z",
  layout: { columns: 24, rowHeight: 40 },
  timeRange: { from: "now-24h", to: "now" },
  variables: [],
  widgets: [
    {
      id: "w1", type: "Stat", title: "Orders today",
      position: { x: 0, y: 0, w: 6, h: 4 },
      source: { kind: "Sql", connectionId: "c1", sql: "SELECT count(*) FROM orders" },
      mapping: {},
      options: { legend: true, thresholds: [] },
    },
  ],
  ...over,
});

const draw = () => render(<MantineProvider><DashboardPanel /></MantineProvider>);

afterEach(cleanup);

describe("a page of widgets", () => {
  beforeEach(() => {
    listDashboards.mockReset().mockResolvedValue({ available: true, dashboards: [dashboard()] });
    listConnections.mockReset().mockResolvedValue([
      { id: "c1", name: "SHOP", engine: "postgresql", readOnly: false, color: null, group: null,
        source: "Stored", summary: "", tunnelled: false },
    ]);
    saveDashboard.mockReset().mockImplementation(async (_id: string, body: Dashboard) => ({
      ...body, id: "d1",
    }));
    deleteDashboard.mockReset().mockResolvedValue(undefined);
    importDashboard.mockReset();
    exportDashboardToGrafana.mockReset().mockResolvedValue("{}");
    runQuery.mockReset().mockImplementation(answers([[42]]));
  });

  it("runs each widget and shows what came back", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("Orders today")).toBeTruthy());
    await waitFor(() => expect(screen.getByText("42")).toBeTruthy());

    // The widget's statement goes through the query path, with the dashboard's range attached.
    const [request] = runQuery.mock.calls[0];
    expect(request.sql).toBe("SELECT count(*) FROM orders");
    expect(request.dashboard).toMatchObject({ from: "now-24h", to: "now" });
  });

  it("says what failed on the widget rather than swallowing it", async () => {
    runQuery.mockImplementation((_request: unknown, onChunk: (chunk: unknown) => void) => {
      onChunk({ type: "error", statement: 0, text: "relation \"orders\" does not exist", code: null, line: null, column: null });
      return { runId: Promise.resolve(null), done: Promise.resolve(), cancel: () => Promise.resolve() };
    });

    draw();

    await waitFor(() => expect(screen.getByText(/does not exist/)).toBeTruthy());
  });

  /// The range is the dashboard's own control, and changing it re-runs the widgets.
  it("re-runs everything when the range changes", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("42")).toBeTruthy());
    runQuery.mockClear();

    const picker = screen.getAllByLabelText("Time range")
      .find(one => one.tagName === "INPUT")!;

    fireEvent.click(picker);
    fireEvent.click(await screen.findByText("Last 7 days"));

    await waitFor(() => expect(runQuery).toHaveBeenCalled());
    expect(runQuery.mock.calls.at(-1)![0].dashboard).toMatchObject({ from: "now-7d" });
  });

  it("keeps a variable's value and sends it with the statement", async () => {
    listDashboards.mockResolvedValue({
      available: true,
      dashboards: [dashboard({
        variables: [{
          name: "region", kind: "Custom", values: ["eu", "us"], multi: false, includeAll: false,
          default: "eu",
        }],
      })],
    });

    draw();

    await waitFor(() => expect(screen.getByText("42")).toBeTruthy());

    expect(runQuery.mock.calls.at(-1)![0].dashboard.variables).toMatchObject({ region: ["eu"] });
  });

  it("edits, adds a widget and saves", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("Orders today")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.click(await screen.findByRole("button", { name: /add widget/i }));
    fireEvent.click(await screen.findByText(/rows as rows/i));

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(saveDashboard).toHaveBeenCalled());

    const [id, body] = saveDashboard.mock.calls[0] as [string, Dashboard];
    expect(id).toBe("d1");
    expect(body.widgets).toHaveLength(2);
    expect(body.widgets[1].type).toBe("Table");
  });

  /// A dashboard the deployment ships is shown and not changed: the way to alter one is a copy.
  it("says a shipped dashboard belongs to the deployment", async () => {
    listDashboards.mockResolvedValue({
      available: true,
      dashboards: [dashboard({ id: "shipped:Ops", name: "Ops", fromFile: true })],
    });

    draw();

    await waitFor(() => expect(screen.getByText(/shipped with the deployment/i)).toBeTruthy());
  });

  it("explains itself when there is nothing to show yet", async () => {
    listDashboards.mockResolvedValue({ available: true, dashboards: [] });

    draw();

    await waitFor(() => expect(screen.getByText(/no dashboard open/i)).toBeTruthy());
  });

  it("says a studio without a workspace cannot keep one", async () => {
    listDashboards.mockResolvedValue({ available: false, dashboards: [] });

    draw();

    await waitFor(() => expect(screen.getByText(/no workspace file/i)).toBeTruthy());
  });

  /// Pasting a Grafana dashboard is the same gesture as pasting one of ours, and what could not
  /// come along is shown rather than swallowed.
  it("imports pasted JSON and shows its notes", async () => {
    importDashboard.mockResolvedValue({
      dashboard: dashboard({ id: "", name: "Pasted" }),
      notes: ["'CPU' queries a metrics datasource"],
    });

    draw();

    await waitFor(() => expect(screen.getByText("Orders today")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "More" }));
    fireEvent.click(await screen.findByText(/paste json/i));

    fireEvent.change(await screen.findByLabelText("The dashboard as JSON"), {
      target: { value: '{"title":"Pasted","panels":[]}' },
    });
    fireEvent.click(screen.getByRole("button", { name: /^import$/i }));

    await waitFor(() => expect(importDashboard).toHaveBeenCalledWith('{"title":"Pasted","panels":[]}'));
    await waitFor(() => expect(screen.getByText(/metrics datasource/i)).toBeTruthy());

    // Nothing was saved: an import is a draft until somebody keeps it.
    expect(saveDashboard).not.toHaveBeenCalled();
  });
});
