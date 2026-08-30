// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { resetFileViewer, viewerAvailable, viewerFrameUrl } from "./fileViewer";

const health = vi.fn();

vi.mock("../api", () => ({ base: "/api", health: () => health() }));

const file = {
  url: "/api/storage/c1/download?ref=x&inline=true",
  name: "quarter.xlsx",
  contentType: "application/vnd.ms-excel",
};

beforeEach(() => {
  resetFileViewer();
  vi.clearAllMocks();
  health.mockResolvedValue({ fileViewer: { script: "https://example.test/mudex.js" } });
});

describe("whether there is a viewer", () => {
  it("is what the server says", async () => {
    expect(await viewerAvailable()).toBe(true);
  });

  it("is asked once, however many files are opened", async () => {
    await viewerAvailable();
    await viewerAvailable();

    expect(health).toHaveBeenCalledTimes(1);
  });

  it("is no for a studio told to do without one", async () => {
    health.mockResolvedValue({ fileViewer: null });
    expect(await viewerAvailable()).toBe(false);
  });

  it("is no for a server too old to have an answer", async () => {
    health.mockResolvedValue({ status: "ok" });
    expect(await viewerAvailable()).toBe(false);
  });

  it("is no when the studio cannot answer, rather than a broken page", async () => {
    health.mockRejectedValue(new Error("offline"));
    expect(await viewerAvailable()).toBe(false);
  });

  it("never loads anything into this page", async () => {
    // The component's stylesheets would repaint the studio and its element has a style property
    // of its own that throws when treated as an element's. Both stay in the frame.
    const append = vi.spyOn(document.head, "appendChild");

    await viewerAvailable();

    expect(append).not.toHaveBeenCalled();
  });
});

describe("the page to frame", () => {
  it("carries the file, its name and its type", () => {
    const url = new URL(viewerFrameUrl(file, false), "http://studio");

    expect(url.pathname).toBe("/api/viewer/frame");
    expect(url.searchParams.get("url")).toBe(file.url);
    expect(url.searchParams.get("name")).toBe(file.name);
    expect(url.searchParams.get("type")).toBe(file.contentType);
    expect(url.searchParams.get("dark")).toBeNull();
  });

  it("says when the studio is dark, so the viewer is not a white rectangle in it", () => {
    const url = new URL(viewerFrameUrl(file, true), "http://studio");
    expect(url.searchParams.get("dark")).toBe("true");
  });

  it("leaves out a type nobody knows", () => {
    const url = new URL(viewerFrameUrl({ url: "/x", name: "a" }, false), "http://studio");
    expect(url.searchParams.has("type")).toBe(false);
  });
});
