// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { cleanup, render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { Security } from "./Security";

const listUsers = vi.fn();
const userGrants = vi.fn();
const previewUserChange = vi.fn();
const applyUserChange = vi.fn();
const applyDdl = vi.fn();

vi.mock("../api", () => ({
  listUsers: (...args: unknown[]) => listUsers(...args),
  userGrants: (...args: unknown[]) => userGrants(...args),
  previewUserChange: (...args: unknown[]) => previewUserChange(...args),
  applyUserChange: (...args: unknown[]) => applyUserChange(...args),
  applyDdl: (...args: unknown[]) => applyDdl(...args),
}));

const principal = (name: string, extra: Record<string, unknown> = {}) => ({
  name, isRole: false, canLogin: true, superuser: false, validUntil: null, locked: false,
  memberOf: [], ...extra,
});

const wrap = () => render(<MantineProvider><Security connectionId="c1" /></MantineProvider>);

describe("the accounts panel", () => {
  beforeEach(() => {
    cleanup();
    listUsers.mockReset();
    userGrants.mockReset();
    previewUserChange.mockReset();
    applyUserChange.mockReset();

    listUsers.mockResolvedValue([
      principal("ada", { memberOf: ["reporting"] }),
      principal("reporting", { isRole: true, canLogin: false }),
      principal("postgres", { superuser: true }),
    ]);
    userGrants.mockResolvedValue([{ object: "public.orders", privilege: "SELECT", grantable: false }]);
    previewUserChange.mockResolvedValue({ hash: "h1", script: "DROP ROLE \"ada\";", destructive: true });
    applyUserChange.mockResolvedValue({ executed: "ok" });
  });

  it("tells an account from a role, and says which roles somebody is in", async () => {
    wrap();

    await waitFor(() => expect(screen.getByText("ada")).toBeTruthy());

    expect(screen.getAllByText("account").length).toBe(2);
    expect(screen.getByText("role")).toBeTruthy();
    expect(screen.getByText("superuser")).toBeTruthy();

    // "member of" is on ada's own row, next to her name.
    const row = screen.getByText("ada").closest("tr")!;
    expect(row.textContent).toContain("reporting");
  });

  /// Listing accounts is itself a privilege. An empty list is a fact about the connection, not an
  /// error somebody can act on.
  it("says why the list is empty rather than showing nothing", async () => {
    listUsers.mockResolvedValue([]);
    wrap();

    await waitFor(() =>
      expect(screen.getByText(/Listing accounts is itself a privilege/)).toBeTruthy());
  });

  it("shows what one of them may do, and where the rest comes from", async () => {
    wrap();

    await waitFor(() => expect(screen.getByText("ada")).toBeTruthy());
    fireEvent.click(screen.getByText("ada"));

    await waitFor(() => expect(userGrants).toHaveBeenCalledWith("c1", "ada"));
    expect(await screen.findByText("public.orders")).toBeTruthy();
  });

  it("runs nothing until the statement was read", async () => {
    wrap();

    await waitFor(() => expect(screen.getByText("ada")).toBeTruthy());
    fireEvent.click(screen.getByLabelText("Change ada"));
    fireEvent.click(await screen.findByText("Drop…"));

    await waitFor(() => expect(previewUserChange).toHaveBeenCalledWith("c1",
      expect.objectContaining({ user: "ada", action: "drop" })));

    // The statement is on screen; nothing has run.
    expect(await screen.findByText('DROP ROLE "ada";')).toBeTruthy();
    expect(applyUserChange).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Run it" }));
    await waitFor(() => expect(applyUserChange).toHaveBeenCalledWith("c1", "h1"));
  });

  it("switches signing in off without touching what the account may do", async () => {
    wrap();

    await waitFor(() => expect(screen.getByText("ada")).toBeTruthy());
    fireEvent.click(screen.getByLabelText("Change ada"));
    fireEvent.click(await screen.findByText("Stop it signing in…"));

    await waitFor(() => expect(previewUserChange).toHaveBeenCalledWith("c1",
      expect.objectContaining({ user: "ada", action: "login", canLogin: false })));
  });

  it("asks for the role a membership needs", async () => {
    wrap();

    await waitFor(() => expect(screen.getByText("ada")).toBeTruthy());
    fireEvent.click(screen.getByLabelText("Change ada"));
    fireEvent.click(await screen.findByText("Put in a role…"));

    // The role field is filled with the first role there is, which is the usual answer.
    const field = await screen.findByPlaceholderText("the role to put it in");
    expect((field as HTMLInputElement).value).toBe("reporting");

    fireEvent.click(screen.getByRole("button", { name: "Show the statement…" }));

    await waitFor(() => expect(previewUserChange).toHaveBeenCalledWith("c1",
      expect.objectContaining({ user: "reporting", action: "grant-role", member: "ada" })));
  });
});
