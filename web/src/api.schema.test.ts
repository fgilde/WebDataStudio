import { describe, expect, it, vi } from "vitest";
import { listSchema } from "./api";

describe("listSchema", () => {
  it("requests the root level without a parent parameter", async () => {
    const fetchMock = vi.fn(async (_url: string) => new Response("[]", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    await listSchema("c1");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/schema/c1");
  });

  it("passes the parent reference through, escaped", async () => {
    const fetchMock = vi.fn(async (_url: string) => new Response("[]", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    await listSchema("c1", "TableFolder:main/tables");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/schema/c1?parent=TableFolder%3Amain%2Ftables");
  });

  it("escapes the object reference when describing", async () => {
    const fetchMock = vi.fn(async (_url: string) => new Response("{}", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    const { describeObject } = await import("./api");
    await describeObject("c1", "Table:main/people");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/schema/c1/object/Table%3Amain%2Fpeople");
  });
});
