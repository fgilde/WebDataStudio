// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { extensionOf, saveAs } from "./saveAs";

describe("extensionOf", () => {
  it("reads the extension a picker needs", () => {
    expect(extensionOf("people.parquet")).toBe(".parquet");
    expect(extensionOf("REPORT.CSV")).toBe(".csv");
  });

  it("and says nothing where there is none", () => {
    expect(extensionOf("dump")).toBe("");
    // A dotfile is a name, not an extension.
    expect(extensionOf(".gitignore")).toBe("");
    expect(extensionOf("trailing.")).toBe("");
  });
});

describe("saveAs", () => {
  let clicked: { href: string; download: string }[];

  beforeEach(() => {
    clicked = [];

    // A real anchor would navigate; what matters is the href and the name it was told to save as.
    vi.spyOn(document, "createElement").mockImplementation(() => {
      const link = {
        href: "",
        download: "",
        click: () => clicked.push({ href: link.href, download: link.download }),
      };

      return link as unknown as HTMLElement;
    });
  });

  it("falls back to the browser's own download where there is no picker", async () => {
    const outcome = await saveAs("/api/storage/s3/download?ref=x", "people.parquet", { picker: {} });

    expect(outcome).toBe("downloaded");
    expect(clicked).toEqual([{ href: "/api/storage/s3/download?ref=x", download: "people.parquet" }]);
  });

  it("writes the file the person picked, streamed", async () => {
    const written: unknown[] = [];
    const pipeTo = vi.fn(() => Promise.resolve());

    const handle = {
      createWritable: () => Promise.resolve({
        write: (chunk: unknown) => { written.push(chunk); return Promise.resolve(); },
        close: () => Promise.resolve(),
      }),
    } as unknown as FileSystemFileHandle;

    const showSaveFilePicker = vi.fn(() => Promise.resolve(handle));
    const fetcher = vi.fn(() => Promise.resolve({
      ok: true, status: 200, body: { pipeTo },
    } as unknown as Response));

    const outcome = await saveAs("/api/storage/s3/download?ref=x", "people.parquet", {
      picker: { showSaveFilePicker }, fetcher, contentType: "application/vnd.apache.parquet",
    });

    expect(outcome).toBe("saved");
    expect(showSaveFilePicker).toHaveBeenCalledWith(
      expect.objectContaining({ suggestedName: "people.parquet" }));
    // Streamed rather than buffered: a file that fits in memory is not the interesting case.
    expect(pipeTo).toHaveBeenCalled();
    expect(written).toEqual([]);
    expect(clicked).toEqual([]);
  });

  it("buffers where the response has no stream to pipe", async () => {
    const written: unknown[] = [];
    const closed = vi.fn(() => Promise.resolve());

    const handle = {
      createWritable: () => Promise.resolve({
        write: (chunk: unknown) => { written.push(chunk); return Promise.resolve(); },
        close: closed,
      }),
    } as unknown as FileSystemFileHandle;

    const fetcher = vi.fn(() => Promise.resolve({
      ok: true, status: 200, body: null, blob: () => Promise.resolve("the bytes"),
    } as unknown as Response));

    const outcome = await saveAs("/x", "dump", {
      picker: { showSaveFilePicker: () => Promise.resolve(handle) }, fetcher,
    });

    expect(outcome).toBe("saved");
    expect(written).toEqual(["the bytes"]);
    expect(closed).toHaveBeenCalled();
  });

  it("closing the dialog saves nothing and downloads nothing", async () => {
    const outcome = await saveAs("/x", "dump", {
      picker: { showSaveFilePicker: () => Promise.reject(new Error("AbortError")) },
      fetcher: vi.fn(),
    });

    expect(outcome).toBe("cancelled");
    expect(clicked).toEqual([]);
  });

  it("says why a file could not be read", async () => {
    const handle = {
      createWritable: () => Promise.resolve({ write: () => Promise.resolve(), close: () => Promise.resolve() }),
    } as unknown as FileSystemFileHandle;

    await expect(saveAs("/x", "dump", {
      picker: { showSaveFilePicker: () => Promise.resolve(handle) },
      fetcher: () => Promise.resolve({ ok: false, status: 404 } as unknown as Response),
    })).rejects.toThrow("404");
  });
});
