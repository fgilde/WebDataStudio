// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { MemoryRouter } from "react-router-dom";

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

const listConnections = vi.fn();

vi.mock("../api", () => ({
  listConnections: () => listConnections(),
  createConnection: vi.fn(),
  deleteConnection: vi.fn(),
  testConnection: vi.fn(),
  connectionPresets: () => Promise.resolve([]),
  entraStatus: vi.fn(),
  entraSignIn: vi.fn(),
  entraSignOut: vi.fn(),
}));

const { ConnectionsPage } = await import("./ConnectionsPage");

const draw = (search: string) => render(
  <MantineProvider>
    <MemoryRouter initialEntries={[`/connections${search}`]}>
      <ConnectionsPage />
    </MemoryRouter>
  </MantineProvider>,
);

describe("ConnectionsPage", () => {
  beforeEach(() => {
    cleanup();
    listConnections.mockReset();
    listConnections.mockResolvedValue([
      {
        id: "c1", name: "LAKE", engine: "storage", readOnly: false, color: null, group: null,
        source: "Environment", summary: "lake/exports", tunnelled: false, interactive: false,
      },
    ]);
  });

  it("lists the connections with where they point", async () => {
    draw("");

    await waitFor(() => expect(screen.getByText("LAKE")).toBeTruthy());
    expect(screen.getByText("lake/exports")).toBeTruthy();
  });

  it("opens the bucket form when the command asked for it", async () => {
    // "Add a bucket" in the palette navigates to /connections?bucket=1. Without this the command
    // opened the page and left somebody looking for the button.
    draw("?bucket=1");

    // The wizard's own copy rather than its title: the page's button carries the same words.
    await waitFor(() => expect(screen.getByText(/anything else speaking S3/)).toBeTruthy());
    expect(screen.getByLabelText("Bucket")).toBeTruthy();
  });

  it("opens the connection form when that is what was asked for", async () => {
    draw("?add=1");

    await waitFor(() => expect(screen.getByLabelText("Connection string")).toBeTruthy());
  });

  it("opens neither on its own", async () => {
    draw("");

    await waitFor(() => expect(screen.getByText("LAKE")).toBeTruthy());
    expect(screen.queryByLabelText("Connection string")).toBeNull();
    expect(screen.queryByText(/anything else speaking S3/)).toBeNull();
  });
});
