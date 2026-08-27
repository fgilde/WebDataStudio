// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { InspectionDialog } from "./InspectionDialog";

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

const findings = [
  {
    id: "update-without-where", severity: "warning",
    message: "This UPDATE has no WHERE: every row in people is changed.",
    statement: 1, line: 3, excerpt: "UPDATE people SET city = 'london'",
  },
  {
    id: "cross-product", severity: "warning",
    message: "2 tables in FROM with nothing joining them",
    statement: 2, line: 5, excerpt: "SELECT * FROM people, orders",
  },
];

describe("InspectionDialog", () => {
  beforeEach(cleanup);

  it("says what it noticed, where, and lets it run anyway", () => {
    const onRun = vi.fn();
    const onCancel = vi.fn();

    render(
      <MantineProvider>
        <InspectionDialog findings={findings} onRun={onRun} onCancel={onCancel} />
      </MantineProvider>,
    );

    expect(screen.getByText(/every row in people is changed/)).toBeTruthy();
    expect(screen.getByText(/nothing joining them/)).toBeTruthy();
    expect(screen.getByText("statement 1, line 3")).toBeTruthy();

    // The run is a plain button and not hidden behind a confirmation phrase: this warns, it does
    // not gate.
    fireEvent.click(screen.getByRole("button", { name: "Run anyway" }));
    expect(onRun).toHaveBeenCalled();
    expect(onCancel).not.toHaveBeenCalled();
  });

  it("goes back to the editor when that is the answer", () => {
    const onCancel = vi.fn();

    render(
      <MantineProvider>
        <InspectionDialog findings={findings} onRun={() => {}} onCancel={onCancel} />
      </MantineProvider>,
    );

    fireEvent.click(screen.getByRole("button", { name: "Back to the editor" }));
    expect(onCancel).toHaveBeenCalled();
  });

  it("says that nothing here is refused", () => {
    render(
      <MantineProvider>
        <InspectionDialog findings={findings} onRun={() => {}} onCancel={() => {}} />
      </MantineProvider>,
    );

    expect(screen.getByText(/Nothing here is refused/)).toBeTruthy();
  });
});
