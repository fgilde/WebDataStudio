// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { FileViewerModal } from "./FileViewerModal";

const viewerAvailable = vi.fn();

vi.mock("./fileViewer", () => ({
  viewerAvailable: () => viewerAvailable(),
  viewerFrameUrl: (file: { url: string; name: string }, dark: boolean) =>
    `/api/viewer/frame?url=${encodeURIComponent(file.url)}&name=${file.name}${dark ? "&dark=true" : ""}`,
}));

const file = {
  url: "/api/storage/c1/download?ref=x&inline=true",
  name: "quarter.xlsx",
  contentType: "application/vnd.ms-excel",
};

const show = (open = true) => render(
  <MantineProvider>
    <FileViewerModal file={open ? file : null} onClose={() => {}} />
  </MantineProvider>);

const frame = () => document.querySelector("iframe");

/// The frame reports on itself, and only the frame's own words count.
const say = (mudex: string, detail?: string, from: Window | null | undefined = undefined) =>
  window.dispatchEvent(new MessageEvent("message", {
    data: { mudex, detail },
    source: (from === undefined ? frame()?.contentWindow : from) as MessageEventSource | null,
  }));

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  viewerAvailable.mockResolvedValue(true);
});

describe("FileViewerModal", () => {
  it("asks for the viewer only when something is opened", () => {
    show(false);
    expect(viewerAvailable).not.toHaveBeenCalled();
  });

  it("keeps the component in a frame, so its stylesheets stay off the studio", async () => {
    show();

    await waitFor(() => expect(frame()).toBeTruthy());

    // Nothing of the component's is in this document: no element, no script, no stylesheet. The
    // WebAssembly runtime also refuses to start in a srcdoc frame, so the page has a real URL.
    expect(document.querySelector("mudex-file-display")).toBeNull();
    expect(document.querySelector("head script[src*='mudex']")).toBeNull();
    expect(frame()!.getAttribute("srcdoc")).toBeNull();
    expect(frame()!.getAttribute("src")).toContain("/api/viewer/frame");
  });

  it("hands the frame the file it is meant to show", async () => {
    show();
    await waitFor(() => expect(frame()).toBeTruthy());

    expect(frame()!.getAttribute("src")).toContain(encodeURIComponent(file.url));
  });

  it("shows the frame once it says it is ready", async () => {
    show();
    await waitFor(() => expect(frame()).toBeTruthy());

    expect(frame()!.style.display).toBe("none");
    expect(screen.getByText(/fetching the viewer/i)).toBeTruthy();

    say("ready");
    await waitFor(() => expect(frame()!.style.display).toBe("block"));
  });

  it("says what the frame said when it fails", async () => {
    show();
    await waitFor(() => expect(frame()).toBeTruthy());

    say("failed", "the viewer could not be fetched");

    await waitFor(() => expect(screen.getByText(/could not be fetched/)).toBeTruthy());
  });

  it("ignores anything that is not this frame talking", async () => {
    show();
    await waitFor(() => expect(frame()).toBeTruthy());

    // Another tab, another frame, an extension: none of them is the viewer reporting on itself.
    say("ready", "", null);
    say("failed", "not from here", null);
    window.dispatchEvent(new MessageEvent("message", { data: "a string" }));

    expect(screen.getByText(/fetching the viewer/i)).toBeTruthy();
  });

  it("says so for a studio with no viewer at all", async () => {
    viewerAvailable.mockResolvedValue(false);
    show();

    await waitFor(() =>
      expect(screen.getByText(/file viewer could not be shown/i)).toBeTruthy());

    expect(frame()).toBeNull();
  });

  it("shows the file's name as the title", async () => {
    show();
    await waitFor(() => expect(screen.getByText("quarter.xlsx")).toBeTruthy());
  });
});
