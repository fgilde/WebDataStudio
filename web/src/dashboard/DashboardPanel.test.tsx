// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { cleanup, render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { DashboardPanel } from "./DashboardPanel";

const listDashboards = vi.fn();
const saveDashboard = vi.fn();
const deleteDashboard = vi.fn();
const listConnections = vi.fn();
const runQuery = vi.fn();

vi.mock("../api", () => ({
  listDashboards: (...args: unknown[]) => listDashboards(...args),
  saveDashboard: (...args: unknown[]) => saveDashboard(...args),
  deleteDashboard: (...args: unknown[]) => deleteDashboard(...args),
  listConnections: (...args: unknown[]) => listConnections(...args),
}));

vi.mock("../query/runQuery", () => ({ runQuery: (...args: unknown[]) => runQuery(...args) }));

/// A run that answers with one row of one number, the way a "how many" tile is used.
const answers = (rows: unknown[][]) => (_request: unknown, onChunk: (chunk: unknown) => void) => {
  onChunk({ type: "columns", statement: 0, columns: [{ name: "n", dataType: "int", nullable: false }] });
  onChunk({ type: "rows", statement: 0, rows });
  onChunk({ type: "end", statement: 0, rowsAffected: 0, elapsedMs: 1, truncated: false });

  return { runId: Promise.resolve("r1"), done: Promise.resolve(), cancel: () => Promise.resolve() };
};

const dashboard = {
  id: "d1", name: "Morning", refreshSeconds: 0, updatedAt: "2026-08-29T08:00:00Z",
  tiles: [{ title: "Orders today", connectionId: "c1", sql: "SELECT count(*)", view: "number", width: 1 }],
};

const wrap = () => render(<MantineProvider><DashboardPanel /></MantineProvider>);

describe("a page of statements", () => {
  beforeEach(() => {
    cleanup();
    listDashboards.mockReset();
    saveDashboard.mockReset();
    runQuery.mockReset();

    listDashboards.mockResolvedValue({ available: true, dashboards: [dashboard] });
    listConnections.mockResolvedValue([{ id: "c1", name: "SHOP", engine: "postgresql" }]);
    runQuery.mockImplementation(answers([[42]]));
  });

  it("runs each tile and shows what came back", async () => {
    wrap();

    expect(await screen.findByText("Orders today")).toBeTruthy();
    expect(await screen.findByText("42")).toBeTruthy();

    // Through the same query path as a query tab, with a cap of its own.
    expect(runQuery).toHaveBeenCalledWith(
      expect.objectContaining({ connectionId: "c1", sql: "SELECT count(*)", maxRows: 200 }),
      expect.any(Function));
  });

  it("shows what failed on the tile rather than swallowing it", async () => {
    runQuery.mockImplementation((_request: unknown, onChunk: (chunk: unknown) => void) => {
      onChunk({ type: "error", statement: 0, text: "relation \"orders\" does not exist", code: null, line: null, column: null });
      return { runId: Promise.resolve(null), done: Promise.resolve(), cancel: () => Promise.resolve() };
    });

    wrap();

    expect(await screen.findByText(/does not exist/)).toBeTruthy();
  });

  it("says why it cannot keep one when there is no workspace", async () => {
    listDashboards.mockResolvedValue({ available: false, dashboards: [] });
    wrap();

    expect(await screen.findByText(/no workspace file/)).toBeTruthy();
  });

  it("explains itself when there is nothing to show yet", async () => {
    listDashboards.mockResolvedValue({ available: true, dashboards: [] });
    wrap();

    expect(await screen.findByText(/a page of statements/i)).toBeTruthy();
  });

  /// A new dashboard opens with one empty tile in it: an editor with nothing in it looks like an
  /// editor that cannot do anything, which is exactly how it read.
  it("saves a new one with the tile it started with", async () => {
    listDashboards.mockResolvedValue({ available: true, dashboards: [] });
    saveDashboard.mockResolvedValue({ ...dashboard, id: "d2", name: "Evening" });

    wrap();

    fireEvent.click(await screen.findByRole("button", { name: "New dashboard" }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "Evening" } });

    // No hunting for "add a tile" first: there is already a row to fill in.
    fireEvent.change(await screen.findByLabelText("Statement of tile 1"),
      { target: { value: "SELECT 1" } });

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(saveDashboard).toHaveBeenCalledWith("", expect.objectContaining({
      name: "Evening",
      tiles: [expect.objectContaining({ sql: "SELECT 1", connectionId: "c1" })],
    })));
  });

  /// The way from "I made a dashboard" to "there is something in it" was a small pencil icon, and
  /// somebody looking for it did not find it.
  it("says what to do next when a dashboard has no tiles", async () => {
    listDashboards.mockResolvedValue({
      available: true,
      dashboards: [{ ...dashboard, tiles: [] }],
    });

    wrap();

    expect(await screen.findByText(/has no tiles yet/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Add the first tile" }));
    expect(await screen.findByLabelText("Statement of tile 1")).toBeTruthy();
  });

  it("puts the tiles of an existing dashboard one click away", async () => {
    wrap();

    fireEvent.click(await screen.findByRole("button", { name: "Edit tiles" }));

    const statement = await screen.findByLabelText("Statement of tile 1");
    expect((statement as HTMLInputElement).value).toBe("SELECT count(*)");
  });
});
