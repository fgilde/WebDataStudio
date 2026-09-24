// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
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

vi.mock("../api", () => ({
  connectionPresets: () => Promise.resolve([]),
  testConnection: vi.fn(),
}));

const { ConnectionForm } = await import("./ConnectionForm");

describe("ConnectionForm", () => {
  it("sends no tunnel when a browser filled only the SSH password", async () => {
    const onSubmit = vi.fn(async (_value: unknown) => {});
    render(<MantineProvider><ConnectionForm onSubmit={onSubmit} onCancel={() => {}} /></MantineProvider>);

    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: "az" } });
    fireEvent.change(screen.getByLabelText("Connection string"),
      { target: { value: "Server=tcp:x.database.windows.net;Database=db;User ID=u;Password='p'" } });
    // What Chrome's saved login does to the collapsed SSH section.
    fireEvent.change(screen.getByLabelText("SSH password"), { target: { value: "saved" } });

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    expect(onSubmit.mock.calls[0][0]).toMatchObject({ engine: "sqlserver", tunnel: null });
  });
});
