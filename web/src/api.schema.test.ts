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

  // An object reference contains a slash, and a reverse proxy in front of a deployed studio
  // decodes %2F back into one before routing — the reference has to be a query value, or the
  // request matches no route and answers 404 in the cloud only.
  it("carries the object reference in the query string, not the path", async () => {
    const fetchMock = vi.fn(async (_url: string) => new Response("{}", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    const { describeObject } = await import("./api");
    await describeObject("c1", "Table:main/people");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/schema/c1/object?ref=Table%3Amain%2Fpeople");
  });

  it("keeps the reference out of the path for rows and DDL too", async () => {
    const fetchMock = vi.fn(async (_url: string) =>
      new Response("{\"columns\":[],\"rows\":[]}", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    const { browseData, loadDdl } = await import("./api");
    await browseData("c1", "Table:main/people", { limit: 10 });
    await loadDdl("c1", "Table:main/people");

    for (const call of fetchMock.mock.calls) {
      const url = String(call[0]);
      expect(url.split("?")[0]).not.toContain("Table");
      expect(url).toContain("ref=Table%3Amain%2Fpeople");
    }
  });
});
