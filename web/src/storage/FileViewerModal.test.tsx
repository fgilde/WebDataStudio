// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { FileViewerModal } from "./FileViewerModal";

const loadFileViewer = vi.fn();

vi.mock("./fileViewer", () => ({ loadFileViewer: () => loadFileViewer() }));

const file = { url: "/api/storage/o?inline=true", name: "quarter.xlsx", contentType: "application/vnd.ms-excel" };

const show = (open = true) => render(
  <MantineProvider>
    <FileViewerModal file={open ? file : null} onClose={() => {}} />
  </MantineProvider>);

const element = () => document.querySelector("mudex-file-display");

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  loadFileViewer.mockResolvedValue(true);
});

describe("FileViewerModal", () => {
  it("fetches the viewer only when something is opened", () => {
    show(false);
    expect(loadFileViewer).not.toHaveBeenCalled();
  });

  it("hands the component the file, dense, with its name and type", async () => {
    show();

    await waitFor(() => expect(element()).toBeTruthy());

    expect(element()!.getAttribute("url")).toBe(file.url);
    expect(element()!.getAttribute("file-name")).toBe(file.name);
    expect(element()!.getAttribute("content-type")).toBe(file.contentType);

    // The modal has the name in its title already.
    expect(element()!.getAttribute("show-file-name")).toBe("false");
    expect(element()!.getAttribute("dense")).toBe("true");
  });

  it("puts the element there by hand, outside what React manages", async () => {
    show();
    await waitFor(() => expect(element()).toBeTruthy());

    // The component starts a WebAssembly runtime and rewrites what is inside its tag. React
    // expecting to own that node is what turned the whole page grey, so the node is not React's.
    const parent = element()!.parentElement!;
    expect(parent.tagName).toBe("DIV");

    // Whatever the component does to its own contents, React has nothing to reconcile.
    element()!.append(document.createElement("span"));
    expect(element()!.childElementCount).toBe(1);
  });

  it("takes the element away again when the file changes", async () => {
    const { rerender } = show();
    await waitFor(() => expect(element()).toBeTruthy());

    rerender(
      <MantineProvider>
        <FileViewerModal file={{ ...file, url: "/other", name: "other.docx" }} onClose={() => {}} />
      </MantineProvider>);

    await waitFor(() => expect(document.querySelectorAll("mudex-file-display")).toHaveLength(1));
    expect(element()!.getAttribute("url")).toBe("/other");
  });

  it("does not take the studio with it when the viewer throws", async () => {
    // Rendering the element is what fails here — the boundary is the difference between a message
    // in a modal and a grey page.
    const create = document.createElement.bind(document);

    vi.spyOn(document, "createElement").mockImplementation(tag => {
      if (tag === "mudex-file-display") throw new Error("boom");
      return create(tag);
    });

    show();

    await waitFor(() =>
      expect(screen.getByText(/file viewer could not be shown/i)).toBeTruthy());
  });

  it("says what happened when the viewer cannot be fetched", async () => {
    loadFileViewer.mockResolvedValue(false);
    show();

    await waitFor(() =>
      expect(screen.getByText(/file viewer could not be shown/i)).toBeTruthy());

    expect(element()).toBeNull();
  });

  it("does not leave an empty box when the fetch throws", async () => {
    loadFileViewer.mockRejectedValue(new Error("offline"));
    show();

    await waitFor(() =>
      expect(screen.getByText(/file viewer could not be shown/i)).toBeTruthy());
  });

  it("shows the file's name as the title", async () => {
    show();
    await waitFor(() => expect(screen.getByText("quarter.xlsx")).toBeTruthy());
  });
});
