// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const login = vi.fn();
vi.mock("../api", () => ({ login: (...args: unknown[]) => login(...args) }));

const { LoginPage } = await import("./LoginPage");

const draw = (sso?: { enabled: boolean; label: string; only: boolean }) =>
  render(
    <MantineProvider>
      <LoginPage sso={sso} onSuccess={() => {}} />
    </MantineProvider>);

describe("LoginPage", () => {
  beforeEach(() => {
    cleanup();
    login.mockReset();
    window.history.replaceState({}, "", "/");
  });

  it("asks for a user and a password where that is the only way in", () => {
    draw();

    expect(screen.getAllByLabelText("User")[0]).toBeTruthy();
    expect(screen.getAllByLabelText("Password")[0]).toBeTruthy();
    expect(screen.queryByText("Single sign-on")).toBeNull();
  });

  it("offers the provider and the form where the deployment has both", () => {
    draw({ enabled: true, label: "Sign in with Entra", only: false });

    const button = screen.getByRole("link", { name: "Sign in with Entra" });
    // A link rather than a fetch: a redirect cannot be followed out of an XMLHttpRequest.
    expect(button.getAttribute("href")).toBe("/api/auth/sso?returnUrl=%2F");
    expect(screen.getAllByLabelText("User")[0]).toBeTruthy();
  });

  it("offers only the provider where there are no local accounts", () => {
    draw({ enabled: true, label: "Single sign-on", only: true });

    expect(screen.getByRole("link", { name: "Single sign-on" })).toBeTruthy();
    expect(screen.queryByLabelText("User")).toBeNull();
    expect(screen.queryByLabelText("Password")).toBeNull();
  });

  it("says so when the provider sent the person back without signing them in", () => {
    window.history.replaceState({}, "", "/?sso=failed");

    draw({ enabled: true, label: "Single sign-on", only: true });

    expect(screen.getByText(/did not complete/)).toBeTruthy();
  });
});
