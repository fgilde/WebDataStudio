import { describe, expect, it, vi, beforeEach } from "vitest";
import { listConnections, login } from "./api";

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
});
