// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { renderHook, act } from "@testing-library/react";
import { useChangeSet } from "./useChangeSet";

const columns = ["id", "name", "active"];
const rows = [[1, "ada", 1], [2, "linus", 1], [3, "grace", 0]];
const setup = () => renderHook(() => useChangeSet(["id"], columns, i => rows[i]));

describe("useChangeSet", () => {
  it("records one update carrying only the edited column", () => {
    const { result } = setup();
    act(() => result.current.edit(0, "name", "changed"));

    expect(result.current.changes).toEqual([
      { kind: "update", key: { id: 1 }, values: { name: "changed" } },
    ]);
  });

  it("keeps one change when the same cell is edited twice", () => {
    const { result } = setup();
    act(() => result.current.edit(0, "name", "one"));
    act(() => result.current.edit(0, "name", "two"));

    expect(result.current.changes).toHaveLength(1);
    expect(result.current.changes[0].values).toEqual({ name: "two" });
  });

  it("drops the change when a cell is edited back to its original value", () => {
    const { result } = setup();
    act(() => result.current.edit(0, "name", "changed"));
    act(() => result.current.edit(0, "name", "ada"));

    expect(result.current.changes).toHaveLength(0);
    expect(result.current.isDirty).toBe(false);
  });

  it("deleting an inserted row removes the insert instead of recording a delete", () => {
    const { result } = setup();
    act(() => result.current.insertRow({ name: "new" }));
    act(() => result.current.deleteRow(-1));

    expect(result.current.changes).toHaveLength(0);
  });

  it("a deleted row does not also produce an update", () => {
    const { result } = setup();
    act(() => result.current.edit(1, "name", "changed"));
    act(() => result.current.deleteRow(1));

    expect(result.current.changes).toEqual([{ kind: "delete", key: { id: 2 }, values: {} }]);
  });

  it("duplicating a row clears the key columns", () => {
    const { result } = setup();
    act(() => result.current.duplicateRow(0, { id: 1, name: "ada", active: 1 }));

    expect(result.current.changes[0]).toEqual({
      kind: "insert", key: {}, values: { name: "ada", active: 1 },
    });
  });

  it("revertAll clears everything", () => {
    const { result } = setup();
    act(() => result.current.edit(0, "name", "x"));
    act(() => result.current.deleteRow(1));
    act(() => result.current.revertAll());

    expect(result.current.isDirty).toBe(false);
  });

  it("reports cell state per cell", () => {
    const { result } = setup();
    act(() => result.current.edit(0, "name", "x"));

    expect(result.current.cellState(0, "name")).toBe("edited");
    expect(result.current.cellState(0, "active")).toBe("clean");
    expect(result.current.cellState(-1, "name")).toBe("inserted");
  });
});
