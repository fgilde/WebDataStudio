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

  it("says what happened when the viewer cannot be fetched", async () => {
    loadFileViewer.mockResolvedValue(false);
    show();

    await waitFor(() =>
      expect(screen.getByText(/file viewer could not be loaded/i)).toBeTruthy());

    expect(element()).toBeNull();
  });

  it("does not leave an empty box when the fetch throws", async () => {
    loadFileViewer.mockRejectedValue(new Error("offline"));
    show();

    await waitFor(() =>
      expect(screen.getByText(/file viewer could not be loaded/i)).toBeTruthy());
  });

  it("shows the file's name as the title", async () => {
    show();
    await waitFor(() => expect(screen.getByText("quarter.xlsx")).toBeTruthy());
  });
});
