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

const health = vi.fn();

vi.mock("../api", () => ({ health: () => health() }));

const { AboutDrawer } = await import("./AboutDrawer");
const { BrandIcon, SHIPPED_ICON } = await import("./BrandIcon");

const draw = (opened = true) => render(
  <MantineProvider>
    <AboutDrawer opened={opened} onClose={() => {}} />
  </MantineProvider>,
);

afterEach(cleanup);

describe("the about drawer", () => {
  beforeEach(() => {
    health.mockReset().mockResolvedValue({
      status: "ok", version: "1.6.1+abcdef1234", commit: "abcdef1234",
      built: "2026-09-09T10:00:00Z", store: { path: "x", available: true, error: null },
      connections: 2,
    });
  });

  it("says which build is running and when it was made", async () => {
    draw();

    await waitFor(() => expect(screen.getByText("v1.6.1")).toBeTruthy());
    expect(screen.getByText("abcdef1")).toBeTruthy();
    expect(screen.getByText("Built")).toBeTruthy();
  });

  it("offers the documentation, the source and the website", async () => {
    draw();

    const href = (name: RegExp) =>
      screen.getByRole("link", { name }).getAttribute("href");

    await waitFor(() => expect(screen.getByRole("link", { name: /documentation/i })).toBeTruthy());

    expect(href(/documentation/i)).toContain("fgilde.github.io/WebDataStudio/guide");
    expect(href(/github/i)).toBe("https://github.com/fgilde/WebDataStudio");
    expect(href(/^website$/i)).toBe("https://fgilde.github.io/WebDataStudio");
    expect(href(/gilde\.org/i)).toBe("https://www.gilde.org");
  });

  /// A drawer nobody opened costs no round trip.
  it("asks the server nothing until it is opened", () => {
    draw(false);

    expect(health).not.toHaveBeenCalled();
  });

  /// A studio too old to answer, or one whose health call fails, still shows the links: they are
  /// the part that does not depend on the server.
  it("keeps the links when the build cannot be read", async () => {
    health.mockRejectedValue(new Error("nope"));
    draw();

    await waitFor(() => expect(screen.getByRole("link", { name: /github/i })).toBeTruthy());
    expect(screen.queryByText(/^v/)).toBeNull();
  });
});

describe("the studio's icon", () => {
  it("shows what the deployment configured", () => {
    render(<BrandIcon src="https://example.com/mine.png" size={40} alt="Studio" />);

    expect(screen.getByAltText("Studio").getAttribute("src"))
      .toBe("https://example.com/mine.png");
  });

  it("shows ours when there is nothing configured", () => {
    render(<BrandIcon src={null} size={40} alt="Studio" />);

    expect(screen.getByAltText("Studio").getAttribute("src")).toBe(SHIPPED_ICON);
  });

  /// A path with a typo in it must not leave a broken image where the logo belongs.
  it("falls back to ours when the configured one does not load", () => {
    render(<BrandIcon src="/brand/not-here.svg" size={40} alt="Studio" />);

    fireEvent.error(screen.getByAltText("Studio"));

    expect(screen.getByAltText("Studio").getAttribute("src")).toBe(SHIPPED_ICON);
  });
});
