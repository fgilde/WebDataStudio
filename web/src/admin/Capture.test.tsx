// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
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

const startCapture = vi.fn();
const captureStatus = vi.fn();
const stopCapture = vi.fn();

vi.mock("../api", () => ({
  startCapture: (...args: unknown[]) => startCapture(...args),
  captureStatus: (...args: unknown[]) => captureStatus(...args),
  stopCapture: (...args: unknown[]) => stopCapture(...args),
}));

const { Capture } = await import("./Capture");

const idle = {
  state: "none", startedAt: null, seconds: 0, secondsLeft: 0, samples: 0,
  statements: [], error: null,
};

const slow = {
  text: "SELECT pg_sleep(2), 'nightly report'", samples: 3, maxDurationMs: 2400,
  firstSeen: "2026-08-27T10:00:00Z", lastSeen: "2026-08-27T10:00:02Z",
  sessions: ["42"], users: ["reports"], databases: ["shop"], blocked: false,
};

const draw = () => render(
  <MantineProvider><Capture connectionId="c1" /></MantineProvider>,
);

describe("Capture", () => {
  beforeEach(() => {
    cleanup();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    startCapture.mockReset();
    captureStatus.mockReset();
    stopCapture.mockReset();
  });

  afterEach(() => vi.useRealTimers());

  it("says what sampling cannot see, before anybody wonders", async () => {
    captureStatus.mockResolvedValue(idle);

    draw();

    await waitFor(() => expect(screen.getByText(/starts and finishes between two samples/)).toBeTruthy());
  });

  it("polls while a capture runs and stops when it is done", async () => {
    captureStatus.mockResolvedValueOnce(idle)
      .mockResolvedValueOnce({ ...idle, state: "running", seconds: 60, secondsLeft: 59, samples: 1 })
      .mockResolvedValue({ ...idle, state: "done", seconds: 60, samples: 60, statements: [slow] });
    startCapture.mockResolvedValue({ ...idle, state: "running", seconds: 60, secondsLeft: 60 });

    draw();

    await waitFor(() => expect(screen.getByRole("button", { name: "Capture" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Capture" }));

    await waitFor(() => expect(startCapture).toHaveBeenCalledWith("c1", 60));

    await vi.advanceTimersByTimeAsync(1100);
    await vi.advanceTimersByTimeAsync(1100);

    await waitFor(() => expect(screen.getByText(/nightly report/)).toBeTruthy());

    const calls = captureStatus.mock.calls.length;
    await vi.advanceTimersByTimeAsync(4000);

    expect(captureStatus.mock.calls.length).toBe(calls);
  });

  it("shows the longest first and how often it was seen", async () => {
    captureStatus.mockResolvedValue({
      ...idle, state: "done", samples: 10,
      statements: [slow, { ...slow, text: "SELECT 1", maxDurationMs: 10, samples: 1, users: ["app"] }],
    });

    draw();

    await waitFor(() => expect(screen.getByText(/nightly report/)).toBeTruthy());
    expect(screen.getByText("2.4s")).toBeTruthy();
    expect(screen.getByText("3×")).toBeTruthy();
    expect(screen.getByText("reports")).toBeTruthy();
  });

  it("picks up a capture that was already running when the panel opened", async () => {
    captureStatus.mockResolvedValue({
      ...idle, state: "running", seconds: 120, secondsLeft: 90, samples: 30, statements: [slow],
    });

    draw();

    await waitFor(() => expect(screen.getByRole("button", { name: /Stop \(90s left\)/ })).toBeTruthy());
  });

  it("stops a running capture on request", async () => {
    captureStatus.mockResolvedValue({ ...idle, state: "running", seconds: 60, secondsLeft: 30 });
    stopCapture.mockResolvedValue({ ...idle, state: "stopped", samples: 30, statements: [slow] });

    draw();

    await waitFor(() => expect(screen.getByRole("button", { name: /Stop/ })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: /Stop/ }));

    await waitFor(() => expect(stopCapture).toHaveBeenCalledWith("c1"));
  });
});
