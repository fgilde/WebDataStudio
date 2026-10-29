import { describe, expect, it, vi, beforeEach } from "vitest";
import { browseData, countRows, listConnections, login } from "./api";

describe("api", () => {
  beforeEach(() => vi.restoreAllMocks());

  it("returns the connection list", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(
      JSON.stringify([{ id: "1", name: "prod", engine: "postgresql", readOnly: true,
                        color: null, group: null, source: "Environment", summary: "db/shop" }]),
      { status: 200, headers: { "content-type": "application/json" } })));

    const list = await listConnections();
    expect(list[0].name).toBe("prod");
  });

  it("surfaces the server message on failure", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(
      JSON.stringify({ message: "invalid credentials" }), { status: 401 })));

    await expect(login("a", "b")).rejects.toThrow("invalid credentials");
  });

  // One pair per column, so the server ANDs them; an emptied filter is not sent at all.
  it("sends each column filter as its own pair", async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ total: 1 }), { status: 200 }));
    vi.stubGlobal("fetch", fetch);
    const filters = { total: ">50", customer_id: "1", note: "" };

    await countRows("c1", "main.orders", { filters });
    await browseData("c1", "main.orders", { filters }).catch(() => {});

    for (const [url] of fetch.mock.calls as unknown as [string][]) {
      const query = new URLSearchParams(url.split("?")[1]);
      expect(query.getAll("filterColumn")).toEqual(["total", "customer_id"]);
      expect(query.getAll("filter")).toEqual([">50", "1"]);
    }
    expect(fetch).toHaveBeenCalledTimes(2);
  });
});
