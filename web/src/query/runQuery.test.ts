import { describe, expect, it, vi } from "vitest";
import { runQuery, type QueryChunk } from "./runQuery";

function streamOf(lines: string[]): Response {
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      const encoder = new TextEncoder();
      // Split one chunk mid-line to prove the reader buffers partial lines.
      const text = lines.join("\n") + "\n";
      controller.enqueue(encoder.encode(text.slice(0, 12)));
      controller.enqueue(encoder.encode(text.slice(12)));
      controller.close();
    },
  });
  return new Response(body, { status: 200, headers: { "X-Run-Id": "run1" } });
}

describe("runQuery", () => {
  it("delivers each chunk in order, even across split reads", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => streamOf([
      '{"type":"columns","statement":0,"columns":[{"name":"id"}]}',
      '{"type":"rows","statement":0,"rows":[[1],[2]]}',
      '{"type":"end","statement":0,"rowsAffected":0,"elapsedMs":3,"truncated":false}',
    ])));

    const seen: QueryChunk[] = [];
    const run = runQuery({ connectionId: "c1", sql: "SELECT id FROM t" }, c => seen.push(c));
    await run.done;

    expect(seen.map(c => c.type)).toEqual(["columns", "rows", "end"]);
    expect((seen[1] as { rows: unknown[][] }).rows).toHaveLength(2);
  });

  it("exposes the run id from the response header", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => streamOf(['{"type":"end","statement":0}'])));
    const run = runQuery({ connectionId: "c1", sql: "SELECT 1" }, () => {});
    await run.done;
    expect(await run.runId).toBe("run1");
  });

  it("ignores a malformed line instead of failing the whole run", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => streamOf([
      "not json",
      '{"type":"end","statement":0}',
    ])));

    const seen: QueryChunk[] = [];
    await runQuery({ connectionId: "c1", sql: "SELECT 1" }, c => seen.push(c)).done;
    expect(seen.map(c => c.type)).toEqual(["end"]);
  });

  it("turns a failed request into an error chunk", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(
      JSON.stringify({ message: "no connection with id 'nope'" }), { status: 404 })));

    const seen: QueryChunk[] = [];
    await runQuery({ connectionId: "nope", sql: "SELECT 1" }, c => seen.push(c)).done;

    expect(seen).toHaveLength(1);
    expect(seen[0].type).toBe("error");
    expect((seen[0] as { text: string }).text).toContain("nope");
  });

  it("posts a cancel for the reported run id", async () => {
    const fetchMock = vi.fn(async (url: string) =>
      url === "/api/query/execute" ? streamOf(['{"type":"end","statement":0}']) : new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    const run = runQuery({ connectionId: "c1", sql: "SELECT 1" }, () => {});
    await run.done;
    await run.cancel();

    expect(fetchMock.mock.calls.map(c => c[0])).toContain("/api/query/run1/cancel");
  });
});
