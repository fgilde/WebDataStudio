// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const historyStats = vi.fn();
vi.mock("../api", () => ({ historyStats: (...args: unknown[]) => historyStats(...args) }));

const { StatementStatsPanel } = await import("./StatementStatsPanel");

const report = {
  days: 30, runs: 42,
  statements: [
    {
      fingerprint: "SELECT count(*) FROM events", example: "SELECT count(*) FROM events",
      runs: 12, failures: 0, averageMs: 2400, slowestMs: 5000, fastestMs: 900,
      firstSeen: "2026-08-01T10:00:00Z", lastSeen: "2026-08-27T10:00:00Z", trend: 2.4,
    },
    {
      fingerprint: "SELECT * FROM people WHERE id = ?", example: "SELECT * FROM people WHERE id = 7",
      runs: 30, failures: 2, averageMs: 30, slowestMs: 90, fastestMs: 10,
      firstSeen: "2026-08-02T10:00:00Z", lastSeen: "2026-08-27T09:00:00Z", trend: null,
    },
  ],
};

const draw = (onOpen?: (sql: string) => void) => render(
  <MantineProvider><StatementStatsPanel connectionId="c1" onOpen={onOpen} /></MantineProvider>,
);

describe("StatementStatsPanel", () => {
  beforeEach(() => {
    cleanup();
    historyStats.mockReset();
  });

  it("shows what ran, how often, and how long it took", async () => {
    historyStats.mockResolvedValue(report);

    draw();

    await waitFor(() => expect(screen.getByText("SELECT count(*) FROM events")).toBeTruthy());
    expect(screen.getByText("42 runs · 2 statements")).toBeTruthy();
    expect(screen.getByText("2.4 s")).toBeTruthy();
    expect(screen.getByText("5.0 s")).toBeTruthy();
    expect(screen.getByText("30 ms")).toBeTruthy();
  });

  it("marks the one that got slower and says nothing where there is no trend", async () => {
    historyStats.mockResolvedValue(report);

    draw();

    await waitFor(() => expect(screen.getByText("2.4×")).toBeTruthy());
    // Two runs are not a trend, and the panel says so with a dash rather than a made-up number.
    expect(screen.getByText("—")).toBeTruthy();
  });

  it("counts the failures next to the runs", async () => {
    historyStats.mockResolvedValue(report);

    draw();

    await waitFor(() => expect(screen.getByText("2 failed")).toBeTruthy());
  });

  it("opens a statement as a query rather than running it", async () => {
    historyStats.mockResolvedValue(report);
    const onOpen = vi.fn();

    draw(onOpen);

    await waitFor(() => expect(screen.getByText("SELECT count(*) FROM events")).toBeTruthy());
    fireEvent.click(screen.getByText("SELECT count(*) FROM events"));

    // The example, not the fingerprint: a statement with `?` in it does not run.
    expect(onOpen).toHaveBeenCalledWith("SELECT count(*) FROM events");
  });

  it("asks for another window when the window changes", async () => {
    historyStats.mockResolvedValue(report);

    draw();

    await waitFor(() => expect(historyStats)
      .toHaveBeenCalledWith({ connectionId: "c1", days: 30 }));

    // Mantine's Select renders an input plus a hidden one, so both carry the label; the input is
    // the one a person clicks.
    fireEvent.click(screen.getAllByLabelText("Window")[0]);
    fireEvent.click(await screen.findByText("last week"));

    await waitFor(() => expect(historyStats)
      .toHaveBeenLastCalledWith({ connectionId: "c1", days: 7 }));
  });

  it("says when nothing ran rather than showing an empty table", async () => {
    historyStats.mockResolvedValue({ days: 30, runs: 0, statements: [] });

    draw();

    await waitFor(() => expect(screen.getByText("Nothing ran in this window.")).toBeTruthy());
  });
});
