// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const tableSizes = vi.fn();
vi.mock("../api", () => ({ tableSizes: (...args: unknown[]) => tableSizes(...args) }));

const { Growth } = await import("./Growth");

const draw = () => render(<MantineProvider><Growth connectionId="c1" /></MantineProvider>);

describe("Growth", () => {
  beforeEach(() => {
    cleanup();
    tableSizes.mockReset();
  });

  it("shows what grew, by how much, and how fast", async () => {
    tableSizes.mockResolvedValue({
      available: true, reason: null, days: 30,
      tables: [{ schema: "public", table: "events", bytes: 2_000_000, rows: 5000 }],
      growth: [{
        schema: "public", table: "events", firstBytes: 1_000_000, lastBytes: 2_000_000,
        from: "2026-07-28T00:00:00Z", to: "2026-08-27T00:00:00Z", rows: 5000,
        delta: 1_000_000, percent: 100, perDay: 33_000,
      }],
    });

    draw();

    await waitFor(() => expect(screen.getByText("public.events")).toBeTruthy());
    expect(screen.getByText("+100%")).toBeTruthy();
    expect(screen.getByText(/\/day/)).toBeTruthy();
  });

  it("says a second look is needed rather than pretending nothing grew", async () => {
    tableSizes.mockResolvedValue({
      available: true, reason: null, days: 30,
      tables: [{ schema: "public", table: "events", bytes: 2_000_000, rows: 5000 }],
      growth: [],
    });

    draw();

    await waitFor(() => expect(screen.getByText(/Growth needs a second look/)).toBeTruthy());
    // The sizes are still worth showing while the history is being built.
    expect(screen.getByText("public.events")).toBeTruthy();
  });

  it("says which engines cannot be asked", async () => {
    tableSizes.mockResolvedValue({
      available: false, reason: "SQLite does not report a size per table", tables: [], growth: [],
    });

    draw();

    await waitFor(() =>
      expect(screen.getByText("SQLite does not report a size per table")).toBeTruthy());
  });

  it("marks a table that shrank differently from one that grew", async () => {
    tableSizes.mockResolvedValue({
      available: true, reason: null, days: 30, tables: [],
      growth: [{
        schema: "public", table: "archive", firstBytes: 9_000_000, lastBytes: 1_000_000,
        from: "2026-07-28T00:00:00Z", to: "2026-08-27T00:00:00Z", rows: 10,
        delta: -8_000_000, percent: -88.9, perDay: -266_000,
      }],
    });

    draw();

    await waitFor(() => expect(screen.getByText("-88.9%")).toBeTruthy());
  });
});
