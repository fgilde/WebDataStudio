// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

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

const listJobs = vi.fn();
const jobHistory = vi.fn();
const jobStatement = vi.fn();

vi.mock("../api", () => ({
  listJobs: (...args: unknown[]) => listJobs(...args),
  jobHistory: (...args: unknown[]) => jobHistory(...args),
  jobStatement: (...args: unknown[]) => jobStatement(...args),
}));

const { Jobs } = await import("./Jobs");

const nightly = {
  id: "1", name: "nightly rebuild", enabled: true, schedule: "nightly",
  lastRun: "2026-08-26T02:00:00Z", lastOutcome: "succeeded", nextRun: "2026-08-28T02:00:00Z",
  command: "ALTER INDEX ALL ON dbo.people REBUILD",
};

const paused = {
  id: "2", name: "paused import", enabled: false, schedule: "", lastRun: null,
  lastOutcome: null, nextRun: null, command: null,
};

const agent = {
  available: true, scheduler: "SQL Server Agent", reason: null,
  jobs: [nightly, paused],
  actions: [
    { id: "enable", label: "Enable", destructive: false },
    { id: "disable", label: "Disable", destructive: false },
    { id: "run", label: "Run now", destructive: true },
  ],
};

const draw = (onOpenInEditor?: (sql: string) => void) => render(
  <MantineProvider><Jobs connectionId="c1" onOpenInEditor={onOpenInEditor} /></MantineProvider>,
);

describe("Jobs", () => {
  beforeEach(() => {
    cleanup();
    listJobs.mockReset();
    jobHistory.mockReset();
    jobStatement.mockReset();
  });

  it("lists what the scheduler runs, with its outcome", async () => {
    listJobs.mockResolvedValue(agent);

    draw();

    await waitFor(() => expect(screen.getByText("nightly rebuild")).toBeTruthy());
    expect(screen.getByText("2 in SQL Server Agent")).toBeTruthy();
    expect(screen.getByText("succeeded")).toBeTruthy();
    expect(screen.getByText("disabled")).toBeTruthy();
  });

  it("offers a disabled job the enable and an enabled one the disable", async () => {
    listJobs.mockResolvedValue(agent);

    draw();

    await waitFor(() => expect(screen.getByText("nightly rebuild")).toBeTruthy());
    // One of each, because the row that is already enabled is not offered "Enable".
    expect(screen.getAllByRole("button", { name: "Enable" })).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: "Disable" })).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: "Run now" })).toHaveLength(2);
  });

  it("hands a change to the editor rather than running it", async () => {
    listJobs.mockResolvedValue(agent);
    jobStatement.mockResolvedValue({ sql: "EXEC msdb.dbo.sp_start_job @job_name = N'paused import'" });
    const opened = vi.fn();

    draw(opened);

    await waitFor(() => expect(screen.getByText("paused import")).toBeTruthy());
    fireEvent.click(screen.getAllByRole("button", { name: "Run now" })[1]);

    await waitFor(() => expect(opened).toHaveBeenCalledWith(
      "EXEC msdb.dbo.sp_start_job @job_name = N'paused import'"));
  });

  it("opens the history of the job that was clicked", async () => {
    listJobs.mockResolvedValue(agent);
    jobHistory.mockResolvedValue([
      { started: "2026-08-26T02:00:00Z", finished: "2026-08-26T02:04:00Z",
        outcome: "succeeded", durationMs: 240000, message: "step 1 finished" },
    ]);

    draw();

    await waitFor(() => expect(screen.getByText("nightly rebuild")).toBeTruthy());
    fireEvent.click(screen.getByText("nightly rebuild"));

    await waitFor(() => expect(screen.getByText("step 1 finished")).toBeTruthy());
    expect(jobHistory).toHaveBeenCalledWith("c1", "1");
  });

  it("says which scheduler an engine does not have instead of showing an empty table", async () => {
    listJobs.mockResolvedValue({
      available: false, scheduler: null, reason: "SQLite has no scheduler of its own",
      jobs: [], actions: [],
    });

    draw();

    await waitFor(() => expect(screen.getByText("SQLite has no scheduler of its own")).toBeTruthy());
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("separates an empty scheduler from a missing one", async () => {
    listJobs.mockResolvedValue({ ...agent, jobs: [] });

    draw();

    await waitFor(() => expect(screen.getByText(/Nothing scheduled/)).toBeTruthy());
  });
});
