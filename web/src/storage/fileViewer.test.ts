// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { loadFileViewer, resetFileViewer, viewerScriptUrl } from "./fileViewer";

const health = vi.fn();

vi.mock("../api", () => ({ health: () => health() }));

/// The loader appends a script and waits for it. Nothing fetches anything in a test, so the event
/// it waits for is fired by hand — and the element it waits for is defined by hand too.
const answerScript = (outcome: "load" | "error") => {
  const append = HTMLHeadElement.prototype.appendChild;

  vi.spyOn(document.head, "appendChild").mockImplementation(node => {
    if (node instanceof HTMLScriptElement) {
      queueMicrotask(() => {
        if (outcome === "load") {
          if (!customElements.get("mudex-file-display"))
            customElements.define("mudex-file-display", class extends HTMLElement {});
        }
        node.dispatchEvent(new Event(outcome));
      });
      return node;
    }

    return append.call(document.head, node) as typeof node;
  });
};

beforeEach(() => {
  resetFileViewer();
  vi.clearAllMocks();
  health.mockResolvedValue({ fileViewer: { script: "https://example.test/mudex.js" } });
});

afterEach(() => vi.restoreAllMocks());

describe("where the viewer comes from", () => {
  it("is what the server says", async () => {
    expect(await viewerScriptUrl()).toBe("https://example.test/mudex.js");
  });

  it("is asked for once, however many files are opened", async () => {
    await viewerScriptUrl();
    await viewerScriptUrl();

    expect(health).toHaveBeenCalledTimes(1);
  });

  it("is nothing for a studio told to do without one", async () => {
    health.mockResolvedValue({ fileViewer: null });
    expect(await viewerScriptUrl()).toBeNull();
  });

  it("is nothing when the studio cannot answer, rather than a broken page", async () => {
    health.mockRejectedValue(new Error("offline"));
    expect(await viewerScriptUrl()).toBeNull();
  });
});

describe("loading it", () => {
  // Order matters here, and it is the browser's rule rather than the test's: a custom element can
  // only be defined once per page, and the loader is right to notice that it is already there. So
  // the failure case runs first, while nothing has defined it yet.
  it("says no when the script cannot be fetched, and does not keep trying", async () => {
    answerScript("error");

    expect(await loadFileViewer()).toBe(false);

    // A studio behind a firewall would otherwise reach for the CDN on every click.
    const appended = document.head.appendChild as unknown as ReturnType<typeof vi.fn>;
    const before = appended.mock.calls.length;

    expect(await loadFileViewer()).toBe(false);
    expect(appended.mock.calls.length).toBe(before);
  });

  it("says no without asking anything when there is no url", async () => {
    health.mockResolvedValue({ fileViewer: null });
    answerScript("load");

    expect(await loadFileViewer()).toBe(false);
    expect(document.head.appendChild).not.toHaveBeenCalled();
  });

  it("loads once for the whole session", async () => {
    answerScript("load");

    const answers = await Promise.all([loadFileViewer(), loadFileViewer(), loadFileViewer()]);

    expect(answers).toEqual([true, true, true]);
    // One load means one question to the server, however many callers there were.
    expect(health).toHaveBeenCalledTimes(1);
  });

  it("takes the element somebody else already put on the page", async () => {
    // A second studio panel, or this one after a hot reload: defined already, nothing to fetch.
    expect(customElements.get("mudex-file-display")).toBeTruthy();

    answerScript("load");
    expect(await loadFileViewer()).toBe(true);
    expect(document.head.appendChild).not.toHaveBeenCalled();
  });
});
