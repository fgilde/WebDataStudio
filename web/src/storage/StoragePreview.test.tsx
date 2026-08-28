// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

// Mantine asks the browser about its colour scheme; jsdom has no answer of its own.
window.matchMedia ??= ((query: string) => ({
  matches: false, media: query, onchange: null,
  addListener: () => {}, removeListener: () => {},
  addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
})) as typeof window.matchMedia;

// Mantine's ScrollArea measures itself; jsdom has no observer to measure with.
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

const previewObject = vi.fn();
const describeObject = vi.fn();
const saveAs = vi.fn();

vi.mock("./saveAs", () => ({ saveAs: (...args: unknown[]) => saveAs(...args) }));

vi.mock("../api", () => ({
  previewObject: (...args: unknown[]) => previewObject(...args),
  describeObject: (...args: unknown[]) => describeObject(...args),
  objectUrl: (conn: string, ref: string) => `/api/storage/${conn}/download?ref=${ref}`,
}));

const { StoragePreview } = await import("./StoragePreview");

const csv = {
  name: "people.csv", key: "exports/people.csv", contentType: "text/csv", size: 16,
  modified: "2026-08-20T10:00:00Z", etag: "abc", storageClass: null,
  queryable: true, from: "read_csv_auto('s3://lake/exports/people.csv')",
  uri: "s3://lake/exports/people.csv", truncated: false, text: "name,age\nada,36\n", binary: false,
};

const draw = () => render(
  <MantineProvider>
    <StoragePreview connectionId="c1" objectRef="StorageObject:lake/exports/people.csv" />
  </MantineProvider>,
);

describe("StoragePreview", () => {
  beforeEach(() => {
    cleanup();
    previewObject.mockReset();
    describeObject.mockReset();
    saveAs.mockReset().mockResolvedValue("saved");
  });

  it("shows the object's facts, its text and the columns it would have as a table", async () => {
    previewObject.mockResolvedValue(csv);
    describeObject.mockResolvedValue({
      columns: [{ name: "name", dataType: "VARCHAR" }, { name: "age", dataType: "BIGINT" }],
      indexes: [], foreignKeys: [], triggers: [],
      rowCount: 2, sizeBytes: 16, comment: null, ddl: null,
    });

    draw();

    await waitFor(() => expect(screen.getByText("people.csv")).toBeTruthy());
    expect(screen.getByText("text/csv")).toBeTruthy();
    expect(screen.getByText(/ada,36/)).toBeTruthy();
    await waitFor(() => expect(screen.getByText("VARCHAR")).toBeTruthy());
  });

  it("does not ask for columns for a file no reader understands", async () => {
    previewObject.mockResolvedValue({
      ...csv, name: "notes.zip", key: "exports/notes.zip", contentType: "application/zip",
      queryable: false, from: null, text: null, binary: true,
    });

    draw();

    await waitFor(() => expect(screen.getByText("notes.zip")).toBeTruthy());
    expect(describeObject).not.toHaveBeenCalled();
    expect(screen.getByText(/Nothing here reads this file/)).toBeTruthy();
  });

  it("offers a download and a save-as, and asks the server for the bytes once", async () => {
    previewObject.mockResolvedValue(csv);
    describeObject.mockResolvedValue({
      columns: [], indexes: [], foreignKeys: [], triggers: [],
      rowCount: null, sizeBytes: 16, comment: null, ddl: null,
    });

    draw();

    await waitFor(() => expect(screen.getByText("people.csv")).toBeTruthy());

    const download = screen.getByRole("link", { name: "Download" });
    expect(download.getAttribute("href"))
      .toBe("/api/storage/c1/download?ref=StorageObject:lake/exports/people.csv");
    expect(download.getAttribute("download")).toBe("people.csv");

    fireEvent.click(screen.getByRole("button", { name: "Save as…" }));

    await waitFor(() => expect(saveAs).toHaveBeenCalled());
    expect(saveAs.mock.calls[0][1]).toBe("people.csv");
  });

  it("shows a PDF where it lies rather than downloading it to be looked at", async () => {
    previewObject.mockResolvedValue({
      ...csv, name: "handbook.pdf", key: "docs/handbook.pdf", contentType: "application/pdf",
      queryable: false, from: null, text: null, binary: true,
    });

    draw();

    await waitFor(() => expect(screen.getByText("handbook.pdf")).toBeTruthy());

    // The bytes the download serves, without the attachment header — otherwise the browser saves the
    // file instead of showing it.
    const embed = document.querySelector("embed");
    expect(embed?.getAttribute("src")).toContain("inline=true");
    // And it is not the "nothing reads this" case any more.
    expect(screen.queryByText(/Nothing here reads this file/)).toBeNull();
  });

  it("plays a recording and a video in place", async () => {
    previewObject.mockResolvedValue({
      ...csv, name: "call.mp3", key: "calls/call.mp3", contentType: "audio/mpeg",
      queryable: false, from: null, text: null, binary: true,
    });

    draw();

    await waitFor(() => expect(screen.getByText("call.mp3")).toBeTruthy());
    expect(document.querySelector("audio")?.getAttribute("src")).toContain("inline=true");
  });

  it("indents a document that arrived on one line", async () => {
    previewObject.mockResolvedValue({
      ...csv, name: "order.json", key: "in/order.json", contentType: "application/json",
      queryable: false, from: null, binary: false,
      text: '{"id":7,"lines":[{"sku":"A"}]}',
    });

    draw();

    await waitFor(() => expect(screen.getByText("order.json")).toBeTruthy());
    expect(screen.getByText(/"sku": "A"/)).toBeTruthy();
  });

  it("leaves a truncated document exactly as it arrived", async () => {
    previewObject.mockResolvedValue({
      ...csv, name: "big.json", key: "in/big.json", contentType: "application/json",
      queryable: false, from: null, binary: false, truncated: true,
      text: '{"id":7,"lines":[{"sku":"A"',
    });

    draw();

    // Half a document is not JSON, and formatting it would drop what was read.
    await waitFor(() => expect(screen.getByText(/\{"id":7/)).toBeTruthy());
  });

  it("says when it read only the front of a file", async () => {
    previewObject.mockResolvedValue({ ...csv, truncated: true });
    describeObject.mockResolvedValue({
      columns: [], indexes: [], foreignKeys: [], triggers: [],
      rowCount: null, sizeBytes: 16, comment: null, ddl: null,
    });

    draw();

    await waitFor(() => expect(screen.getByText(/first .* only/)).toBeTruthy());
  });
});
