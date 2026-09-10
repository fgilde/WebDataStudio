// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import type { StudioUsersDto } from "../api";

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

const listStudioUsers = vi.fn();
const createStudioUser = vi.fn();
const updateStudioUser = vi.fn();
const deleteStudioUser = vi.fn();

vi.mock("../api", () => ({
  listStudioUsers: () => listStudioUsers(),
  hashStudioPassword: vi.fn(async () => ({ hash: "pbkdf2$1$a$b" })),
  createStudioUser: (...args: unknown[]) => createStudioUser(...args),
  updateStudioUser: (...args: unknown[]) => updateStudioUser(...args),
  deleteStudioUser: (...args: unknown[]) => deleteStudioUser(...args),
}));

const { StudioUsers } = await import("./StudioUsers");

const state = (over: Partial<StudioUsersDto> = {}): StudioUsersDto => ({
  anonymous: false,
  source: "WDS_USERS",
  writable: true,
  users: [
    { name: "boss", role: "admin", connections: [], hashed: true, source: "Environment" },
    { name: "clara", role: "editor", connections: ["SHOP"], hashed: true, source: "Stored" },
  ],
  ...over,
});

const draw = () => render(
  <MantineProvider>
    <StudioUsers />
  </MantineProvider>,
);

afterEach(cleanup);

describe("the accounts tab", () => {
  beforeEach(() => {
    listStudioUsers.mockReset().mockResolvedValue(state());
    createStudioUser.mockReset().mockResolvedValue({ name: "new", role: "editor" });
    updateStudioUser.mockReset().mockResolvedValue(undefined);
    deleteStudioUser.mockReset().mockResolvedValue(undefined);
  });

  it("says where each account comes from", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("clara")).toBeTruthy());

    expect(screen.getByText(/from the environment/i)).toBeTruthy();
  });

  /// The deployment owns its own accounts: no pencil, no bin, and the row says why.
  it("offers no editing for an account the environment owns", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("boss")).toBeTruthy());

    expect(screen.queryByRole("button", { name: /edit boss/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /delete boss/i })).toBeNull();
    expect(screen.getByRole("button", { name: /edit clara/i })).toBeTruthy();
  });

  it("makes an account", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("clara")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: /add account/i }));

    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "mallory" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "s3cret" } });
    fireEvent.click(screen.getByRole("button", { name: /^create$/i }));

    await waitFor(() => expect(createStudioUser).toHaveBeenCalled());

    const [sent] = createStudioUser.mock.calls[0];
    expect(sent).toMatchObject({ name: "mallory", password: "s3cret" });
  });

  /// An edit with the password left alone must not send an empty one: on the server that means
  /// "keep it", and sending "" would be the same thing said by accident.
  it("edits a role without touching the password", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("clara")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: /edit clara/i }));
    fireEvent.click(await screen.findByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(updateStudioUser).toHaveBeenCalled());

    const [name, sent] = updateStudioUser.mock.calls[0] as [string, { password?: string }];
    expect(name).toBe("clara");
    expect(sent.password).toBeUndefined();
  });

  it("removes an account after asking", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("clara")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: /delete clara/i }));
    fireEvent.click(await screen.findByRole("button", { name: /^delete$/i }));

    await waitFor(() => expect(deleteStudioUser).toHaveBeenCalledWith("clara"));
  });

  /// A studio whose data directory cannot be written says so instead of offering a button that
  /// fails.
  it("offers nothing to add where the store is unusable", async () => {
    listStudioUsers.mockResolvedValue(state({ writable: false }));
    draw();

    await waitFor(() => expect(screen.getByText("clara")).toBeTruthy());

    expect(screen.queryByRole("button", { name: /add account/i })).toBeNull();
  });

  /// An anonymous studio is the one place the first account gets made, so the tab has to offer it
  /// there rather than only explaining the environment variable.
  it("lets the first account be made on a studio with none", async () => {
    listStudioUsers.mockResolvedValue(state({ anonymous: true, users: [] }));
    draw();

    await waitFor(() => expect(screen.getByRole("button", { name: /add account/i })).toBeTruthy());
    expect(screen.getByText(/no accounts yet/i)).toBeTruthy();
  });

  it("says what went wrong instead of throwing it", async () => {
    createStudioUser.mockRejectedValue(new Error("an account named 'mallory' already exists"));
    draw();

    await waitFor(() => expect(screen.getByText("clara")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: /add account/i }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "mallory" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "s3cret" } });
    fireEvent.click(screen.getByRole("button", { name: /^create$/i }));

    await waitFor(() => expect(screen.getByText(/already exists/i)).toBeTruthy());
  });
});
