// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

// Mantine asks the browser about its colour scheme; jsdom has no answer of its own.
window.matchMedia ??= ((query: string) => ({
  matches: false, media: query, onchange: null,
  addListener: () => {}, removeListener: () => {},
  addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
})) as typeof window.matchMedia;

// Mantine's ScrollArea measures itself; jsdom has no observer to measure with.
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

const distinctValues = vi.fn();
vi.mock("../api", () => ({ distinctValues: (...args: unknown[]) => distinctValues(...args) }));

const { DistinctValues } = await import("./DistinctValues");

const values = [
  { value: "shipped", count: 3 },
  { value: "open", count: 3 },
  { value: "refunded", count: 1 },
  { value: "cancelled", count: 1 },
  { value: null, count: 2 },
];

const setup = (onPick = vi.fn()) => {
  distinctValues.mockResolvedValue({ masked: false, values, truncated: false });

  render(
    <MantineProvider>
      <DistinctValues connectionId="c1" objectRef="Table:main/orders" column="status"
        onPick={onPick} />
    </MantineProvider>,
  );

  return onPick;
};

describe("DistinctValues", () => {
  // Vitest is not running with globals, so the automatic cleanup is not wired up: without this
  // every test renders on top of the last one's DOM.
  beforeEach(() => { cleanup(); distinctValues.mockReset(); });

  it("survives being ticked more often than twice", async () => {
    // The report: after two or three ticks the whole studio went grey. The handler read
    // `event.currentTarget` inside the state updater, and React runs that updater after the event
    // is done with — so the third tick threw and took the React root with it.
    const onPick = setup();

    await waitFor(() => expect(screen.getByLabelText(/shipped/)).toBeTruthy());

    for (const label of [/shipped/, /open/, /refunded/, /cancelled/])
      fireEvent.click(screen.getByLabelText(label));

    // Still rendered, and every tick counted.
    fireEvent.click(screen.getByRole("button", { name: /Filter by 4/ }));

    expect(onPick).toHaveBeenCalledWith("=shipped,=open,=refunded,=cancelled");
  });

  it("can untick as well as tick", async () => {
    const onPick = setup();

    await waitFor(() => expect(screen.getByLabelText(/shipped/)).toBeTruthy());

    fireEvent.click(screen.getByLabelText(/shipped/));
    fireEvent.click(screen.getByLabelText(/open/));
    fireEvent.click(screen.getByLabelText(/shipped/));

    // The button counts what is ticked, so the count is part of the answer.
    const apply = screen.getByRole("button", { name: /Filter by/ });
    expect(apply.textContent).toContain("1");

    fireEvent.click(apply);
    expect(onPick).toHaveBeenCalledWith("=open");
  });

  it("asks for the rows with no value at all rather than for an empty one", async () => {
    const onPick = setup();

    await waitFor(() => expect(screen.getByLabelText(/NULL/)).toBeTruthy());

    fireEvent.click(screen.getByLabelText(/NULL/));
    fireEvent.click(screen.getByRole("button", { name: /Filter by 1/ }));

    expect(onPick).toHaveBeenCalledWith("NULL");
  });

  it("says nothing about a masked column, because its values are the secret", async () => {
    distinctValues.mockResolvedValue({ masked: true, values: [], truncated: false });

    render(
      <MantineProvider>
        <DistinctValues connectionId="c1" objectRef="Table:main/people" column="api_token"
          onPick={vi.fn()} />
      </MantineProvider>,
    );

    await waitFor(() => expect(screen.getByText(/masked/)).toBeTruthy());
    expect(screen.queryByRole("checkbox")).toBeNull();
  });
});
