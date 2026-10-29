// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
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
const updateConnection = vi.fn();

/// What the studio allows. Permissive unless a test says otherwise, which is also the default the
/// server answers with.
const access = {
  scope: "Stored" as "Stored" | "Session",
  mayAdd: true,
  mayUpload: true,
  mayBrowse: true,
};

const me = vi.fn(async () => ({
  anonymous: true, authenticated: true, username: null, access,
}));

vi.mock("../api", () => ({
  me: () => me(),
  forgetSession: vi.fn(),
  listConnections: () => listConnections(),
  createConnection: vi.fn(),
  deleteConnection: vi.fn(),
  updateConnection: (...args: unknown[]) => updateConnection(...args),
  revealConnectionString: () => Promise.resolve("Host=db;Password=pw"),
  testConnection: vi.fn(),
  connectionPresets: () => Promise.resolve([]),
  entraStatus: vi.fn(),
  entraSignIn: vi.fn(),
  entraSignOut: vi.fn(),
}));

const { ConnectionsPage } = await import("./ConnectionsPage");
const { forgetAccess } = await import("./useAccess");

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

  it("edits a stored connection in the form it was made in", async () => {
    const shop = {
      id: "c2", name: "SHOP", engine: "postgresql", readOnly: false, color: null, group: null,
      source: "Stored", summary: "db/shop", tunnelled: false, interactive: false,
    };
    listConnections.mockResolvedValue([shop]);
    updateConnection.mockResolvedValue({ ...shop, name: "SHOP2" });
    draw("");

    fireEvent.click(await screen.findByLabelText("Edit SHOP"));
    const name = await screen.findByDisplayValue("SHOP");
    expect(screen.getByDisplayValue("Host=db;Password=pw")).toBeTruthy();

    fireEvent.change(name, { target: { value: "SHOP2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateConnection).toHaveBeenCalledWith("c2",
      expect.objectContaining({ name: "SHOP2", connectionString: "Host=db;Password=pw" })));
    await waitFor(() => expect(screen.getByText("SHOP2")).toBeTruthy());
  });

  /// What this deployment allows decides what the page offers: a button for a closed door would
  /// only answer with a refusal.
  describe("what the deployment allows", () => {
    beforeEach(() => {
      forgetAccess();
      access.scope = "Stored";
      access.mayAdd = true;
    });

    it("offers no Add button where connections do not come from users", async () => {
      access.mayAdd = false;
      draw("");

      await waitFor(() => expect(screen.getByText("LAKE")).toBeTruthy());
      expect(screen.queryByRole("button", { name: /add connection/i })).toBeNull();
      expect(screen.queryByRole("button", { name: /add a bucket/i })).toBeNull();
    });

    it("offers it where they do", async () => {
      draw("");

      await waitFor(() => expect(screen.getByRole("button", { name: /add connection/i })).toBeTruthy());
    });

    it("offers a way out where connections belong to this browser", async () => {
      access.scope = "Session";
      draw("");

      await waitFor(() =>
        expect(screen.getByRole("button", { name: /forget my connections/i })).toBeTruthy());
    });

    it("offers no way out where they are everybody's", async () => {
      draw("");

      await waitFor(() => expect(screen.getByText("LAKE")).toBeTruthy());
      expect(screen.queryByRole("button", { name: /forget my connections/i })).toBeNull();
    });

    /// The empty state is the whole instruction a visitor gets on a studio like that.
    it("says what to bring when there is nothing yet", async () => {
      access.scope = "Session";
      listConnections.mockResolvedValue([]);
      draw("");

      await waitFor(() =>
        expect(screen.getByRole("button", { name: /bring a database/i })).toBeTruthy());
      expect(screen.getByText(/belongs to this browser/i)).toBeTruthy();
      expect(screen.getByText(/from your own machine/i)).toBeTruthy();
    });
  });
});
