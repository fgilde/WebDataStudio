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

const entraSignIn = vi.fn();
const entraStatus = vi.fn();
const entraSignOut = vi.fn();

vi.mock("../api", () => ({
  entraSignIn: (...args: unknown[]) => entraSignIn(...args),
  entraStatus: (...args: unknown[]) => entraStatus(...args),
  entraSignOut: (...args: unknown[]) => entraSignOut(...args),
}));

const { EntraSignInModal } = await import("./EntraSignInModal");

const none = {
  state: "none", userCode: null, verificationUrl: null, message: null,
  expiresOn: null, error: null,
};

const pending = {
  ...none, state: "pending", userCode: "ABCD-EFGH",
  verificationUrl: "https://microsoft.com/devicelogin",
  message: "Enter ABCD-EFGH at https://microsoft.com/devicelogin",
};

const draw = () => render(
  <MantineProvider>
    <EntraSignInModal connectionId="c1" name="Azure SQL" opened onClose={() => {}} />
  </MantineProvider>,
);

describe("EntraSignInModal", () => {
  beforeEach(() => {
    cleanup();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    entraSignIn.mockReset();
    entraStatus.mockReset();
    entraSignOut.mockReset();
  });

  afterEach(() => vi.useRealTimers());

  it("shows the code and where to enter it, and never a token", async () => {
    entraStatus.mockResolvedValue(none);
    entraSignIn.mockResolvedValue(pending);

    draw();

    await waitFor(() => expect(screen.getByRole("button", { name: "Sign in" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(screen.getByText("ABCD-EFGH")).toBeTruthy());
    expect(screen.getByText("https://microsoft.com/devicelogin")).toBeTruthy();
  });

  it("polls until the sign-in is done and then stops", async () => {
    entraStatus.mockResolvedValueOnce(none)
      .mockResolvedValueOnce(pending)
      .mockResolvedValue({ ...none, state: "signed-in", expiresOn: "2026-08-27T12:00:00Z" });
    entraSignIn.mockResolvedValue({ ...none, state: "starting" });

    draw();

    await waitFor(() => expect(screen.getByRole("button", { name: "Sign in" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await vi.advanceTimersByTimeAsync(2100);
    await vi.advanceTimersByTimeAsync(2100);

    await waitFor(() => expect(screen.getByText("signed in")).toBeTruthy());

    const calls = entraStatus.mock.calls.length;
    await vi.advanceTimersByTimeAsync(6000);

    // The poll is stopped, not merely ignored: an idle modal must not keep asking.
    expect(entraStatus.mock.calls.length).toBe(calls);
  });

  it("offers a sign-out once somebody is signed in", async () => {
    entraStatus.mockResolvedValue({ ...none, state: "signed-in" });
    entraSignOut.mockResolvedValue(undefined);

    draw();

    await waitFor(() => expect(screen.getByRole("button", { name: "Sign out" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Sign out" }));

    await waitFor(() => expect(entraSignOut).toHaveBeenCalledWith("c1"));
  });

  it("says when the last sign-in has expired", async () => {
    entraStatus.mockResolvedValue({ ...none, state: "expired" });

    draw();

    await waitFor(() => expect(screen.getByText(/has expired/)).toBeTruthy());
  });

  it("shows what went wrong instead of a blank modal", async () => {
    entraStatus.mockResolvedValue({ ...none, state: "failed", error: "the code expired" });

    draw();

    await waitFor(() => expect(screen.getByText("the code expired")).toBeTruthy());
  });
});
