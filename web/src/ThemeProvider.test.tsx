// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { AppThemeProvider, useAppTheme } from "./ThemeProvider";
import { THEME_KEY } from "./themes";

const me = vi.fn();
vi.mock("./api", () => ({ me: () => me() }));

function Shows() {
  const { themeId } = useAppTheme();
  return <div data-testid="theme">{themeId}</div>;
}

const shown = () => screen.getByTestId("theme").textContent;

describe("the theme a studio comes up in", () => {
  beforeEach(() => {
    cleanup();
    localStorage.clear();
    me.mockReset();
    me.mockResolvedValue({ anonymous: true, authenticated: true, username: null });
  });

  afterEach(() => localStorage.clear());

  it("is the studio's own default when nothing said otherwise", async () => {
    render(<AppThemeProvider><Shows /></AppThemeProvider>);

    expect(shown()).toBe("ocean");
    await waitFor(() => expect(me).toHaveBeenCalled());
    expect(shown()).toBe("ocean");
  });

  /// WithTheme(WebDataStudioTheme.Nord) in an Aspire stack, WDS_THEME=nord anywhere else.
  it("follows what the deployment asked for", async () => {
    me.mockResolvedValue({ anonymous: true, authenticated: true, username: null, theme: "nord" });

    render(<AppThemeProvider><Shows /></AppThemeProvider>);

    await waitFor(() => expect(shown()).toBe("nord"));

    // Not written down: a later change to the deployment's default has to reach this browser too.
    expect(localStorage.getItem(THEME_KEY)).toBeNull();
  });

  it("leaves a person's own choice alone", async () => {
    localStorage.setItem(THEME_KEY, "dracula");
    me.mockResolvedValue({ anonymous: true, authenticated: true, username: null, theme: "nord" });

    render(<AppThemeProvider><Shows /></AppThemeProvider>);

    await waitFor(() => expect(me).not.toHaveBeenCalled());
    expect(shown()).toBe("dracula");
  });

  it("ignores a theme it does not have rather than showing nothing", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    me.mockResolvedValue({ anonymous: true, authenticated: true, username: null, theme: "chartreuse" });

    render(<AppThemeProvider><Shows /></AppThemeProvider>);

    await waitFor(() => expect(warn).toHaveBeenCalled());
    expect(shown()).toBe("ocean");
    warn.mockRestore();
  });
});
