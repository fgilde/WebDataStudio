// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
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
