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

const schemaScope = vi.fn();
const chooseSchemas = vi.fn();

vi.mock("../api", () => ({
  schemaScope: (...args: unknown[]) => schemaScope(...args),
  chooseSchemas: (...args: unknown[]) => chooseSchemas(...args),
}));

const { SchemaScopePicker } = await import("./SchemaScopePicker");

const draw = (onChanged?: () => void) => render(
  <MantineProvider><SchemaScopePicker connectionId="c1" onChanged={onChanged} /></MantineProvider>,
);

describe("SchemaScopePicker", () => {
  beforeEach(() => {
    cleanup();
    schemaScope.mockReset();
    chooseSchemas.mockReset();
  });

  it("offers every schema and says that nothing chosen means all of them", async () => {
    schemaScope.mockResolvedValue({
      available: ["public", "sales", "archive"], chosen: [], fixedByEnvironment: [], editable: true,
    });

    draw();

    await waitFor(() => expect(screen.getByText("Schemas read")).toBeTruthy());
    expect(screen.getByPlaceholderText("all of them")).toBeTruthy();
  });

  it("saves what was chosen and tells the shell to re-read the tree", async () => {
    schemaScope.mockResolvedValue({
      available: ["public", "sales"], chosen: ["sales"], fixedByEnvironment: [], editable: true,
    });
    chooseSchemas.mockResolvedValue({ chosen: ["sales"] });
    const onChanged = vi.fn();

    draw(onChanged);

    await waitFor(() => expect(screen.getByRole("button", { name: "Apply" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(chooseSchemas).toHaveBeenCalledWith("c1", ["sales"]));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it("says so rather than pretending to be editable where the environment fixed the scope", async () => {
    schemaScope.mockResolvedValue({
      available: ["public", "sales"], chosen: [], fixedByEnvironment: ["public"], editable: false,
    });

    draw();

    await waitFor(() => expect(screen.getByText(/Fixed by the environment: public/)).toBeTruthy());
    expect(screen.queryByRole("button", { name: "Apply" })).toBeNull();
  });
});
