import { describe, expect, it } from "vitest";
import { applyChunk, createResultState } from "./resultStore";

describe("resultStore", () => {
  it("accumulates rows across chunks", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "columns", statement: 0, columns: [{ name: "id", dataType: "int", nullable: false }] });
    state = applyChunk(state, { type: "rows", statement: 0, rows: [[1]] });
    state = applyChunk(state, { type: "rows", statement: 0, rows: [[2]] });

    expect(state.statements[0].rows).toEqual([[1], [2]]);
    expect(state.statements[0].running).toBe(true);
  });

  it("marks a statement finished and records its timing", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "columns", statement: 0, columns: [] });
    state = applyChunk(state, { type: "end", statement: 0, rowsAffected: 3, elapsedMs: 42, truncated: true });

    expect(state.statements[0].running).toBe(false);
    expect(state.statements[0].elapsedMs).toBe(42);
    expect(state.statements[0].truncated).toBe(true);
  });

  it("keeps statements separate", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "rows", statement: 0, rows: [[1]] });
    state = applyChunk(state, { type: "rows", statement: 1, rows: [[9]] });

    expect(state.statements).toHaveLength(2);
    expect(state.statements[1].rows).toEqual([[9]]);
  });

  it("stores an error on its statement", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "error", statement: 0, text: "boom", code: "42601", line: 2, column: 5 });

    expect(state.statements[0].error).toEqual({ text: "boom", code: "42601", line: 2, column: 5 });
    expect(state.statements[0].running).toBe(false);
  });

  it("collects messages without losing the statement they belong to", () => {
    let state = createResultState();
    state = applyChunk(state, { type: "message", statement: 1, severity: "notice", text: "hi" });

    expect(state.messages).toEqual([{ statement: 1, severity: "notice", text: "hi" }]);
  });

  it("records cancellation", () => {
    const state = applyChunk(createResultState(), { type: "cancelled" });
    expect(state.cancelled).toBe(true);
  });
});
