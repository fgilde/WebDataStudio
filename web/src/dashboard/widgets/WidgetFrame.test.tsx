// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import type { Dashboard, Widget } from "../model";

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

Element.prototype.scrollIntoView ??= () => {};

const runQuery = vi.fn();

vi.mock("../../query/runQuery", () => ({ runQuery: (...args: unknown[]) => runQuery(...args) }));
vi.mock("../../federate/runFederation", () => ({ runFederation: vi.fn(async () => {}) }));
vi.mock("../charts/EChart", () => ({ EChart: () => <div data-testid="chart" /> }));

const { WidgetFrame } = await import("./WidgetFrame");

const answers = (columns: string[], rows: unknown[][]) =>
  (_request: unknown, onChunk: (chunk: unknown) => void) => {
    onChunk({
      type: "columns", statement: 0,
      columns: columns.map(name => ({ name, dataType: "text", nullable: true })),
    });
    onChunk({ type: "rows", statement: 0, rows });
    onChunk({ type: "end", statement: 0, rowsAffected: 0, elapsedMs: 1, truncated: false });

    return { runId: Promise.resolve("r1"), done: Promise.resolve(), cancel: () => Promise.resolve() };
  };

const dashboard: Dashboard = {
  id: "d1", name: "Morning", widgets: [], refreshSeconds: 0, updatedAt: "",
  layout: { columns: 24, rowHeight: 40 }, timeRange: { from: "now-24h", to: "now" }, variables: [],
};

const widget = (over: Partial<Widget> = {}): Widget => ({
  id: "w1", type: "Stat", title: "Customers",
  position: { x: 0, y: 0, w: 6, h: 4 },
  source: { kind: "Sql", connectionId: "c1", sql: "SELECT count(*) AS n FROM customers" },
  mapping: {},
  options: { legend: true, thresholds: [] },
  ...over,
});

const draw = (one: Widget, chosen: Record<string, string[]> = {}) => render(
  <MantineProvider>
    <WidgetFrame widget={one} dashboard={dashboard} chosen={chosen} dark={false} editing={false}
      nonce={0} />
  </MantineProvider>,
);

afterEach(cleanup);

describe("a widget", () => {
  beforeEach(() => {
    runQuery.mockReset().mockImplementation(answers(["n"], [[42]]));
  });

  it("shows its number, and the state a threshold means", async () => {
    draw(widget({
      options: {
        legend: true,
        thresholds: [{ value: 10, level: "warning" }, { value: 100, level: "critical" }],
      },
    }));

    await waitFor(() => expect(screen.getByText("42")).toBeTruthy());
    // The state is a word as well, so it is never carried by colour alone.
    expect(screen.getByText("warning")).toBeTruthy();
  });

  it("says what failed, in its own frame", async () => {
    runQuery.mockImplementation((_request: unknown, onChunk: (chunk: unknown) => void) => {
      onChunk({
        type: "error", statement: 0, text: "no such table: customers",
        code: null, line: null, column: null,
      });
      return { runId: Promise.resolve(null), done: Promise.resolve(), cancel: () => Promise.resolve() };
    });

    draw(widget());

    await waitFor(() => expect(screen.getByText(/no such table/)).toBeTruthy());
  });

  /// The accessibility relief and the debugging tool in one switch.
  it("turns a chart into the rows it was made of", async () => {
    runQuery.mockImplementation(answers(["status", "orders"], [["paid", 12], ["open", 5]]));

    draw(widget({ type: "Bar", title: "Orders by status" }));

    await waitFor(() => expect(screen.getByTestId("chart")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: /menu for orders by status/i }));
    fireEvent.click(await screen.findByText(/show the rows/i));

    await waitFor(() => expect(screen.getByText("paid")).toBeTruthy());
    expect(screen.queryByTestId("chart")).toBeNull();
  });

  /// A text widget's content comes from a dashboard file or somebody else's export, so it renders
  /// as elements and never as HTML.
  it("renders Markdown without letting HTML through", async () => {
    draw(widget({
      type: "Text",
      title: "How to read this",
      options: {
        legend: true,
        markdown: "## Orders\n\nCounted when **paid**. Region: $region\n\n"
          + "<img src=x onerror=\"alert(1)\">\n\n- one\n- two",
      },
    }), { region: ["eu", "us"] });

    await waitFor(() => expect(screen.getByText("Orders")).toBeTruthy());

    expect(screen.getByText(/paid/)).toBeTruthy();
    // The variable is filled in…
    expect(screen.getByText(/Region: eu, us/)).toBeTruthy();
    // …and the tag is characters on the page rather than an element.
    expect(screen.getByText(/<img src=x/)).toBeTruthy();
    expect(document.querySelector("img")).toBeNull();
    expect(screen.getByText("one")).toBeTruthy();
  });

  /// A band names a group of widgets and has no statement of its own.
  it("draws a section band without running anything", () => {
    draw(widget({ type: "Row", title: "Overview" }));

    expect(screen.getByText("Overview")).toBeTruthy();
    expect(runQuery).not.toHaveBeenCalled();
  });

  it("runs nothing until it has a connection and a statement", () => {
    draw(widget({ source: { kind: "Sql", connectionId: null, sql: "" } }));

    expect(runQuery).not.toHaveBeenCalled();
  });
});
