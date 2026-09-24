// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import type { StatementResult } from "../query/resultStore";

vi.mock("../api", () => ({
  loadWorkspaceItem: () => Promise.resolve(null),
  saveWorkspaceItem: () => Promise.resolve(),
}));

// The virtualiser measures its scroll box; jsdom has none, so give every element a size.
Object.defineProperty(HTMLElement.prototype, "offsetHeight", { configurable: true, get: () => 600 });
Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get: () => 800 });

const { ResultGrid } = await import("./ResultGrid");

const result = (rows: unknown[][], truncated = false): StatementResult => ({
  index: 0, columns: [{ name: "id", dataType: "int" }] as StatementResult["columns"], rows,
  documents: [], rowsAffected: null, elapsedMs: 5, rowsRead: rows.length, truncated,
  error: null, running: false,
});

const draw = (r: StatementResult, onFetchAll?: () => void) => render(
  <MantineProvider><ResultGrid result={r} onFetchAll={onFetchAll} /></MantineProvider>);

describe("ResultGrid", () => {
  beforeEach(() => cleanup());

  it("numbers the rows from one, like every other studio", () => {
    draw(result([[30], [10], [20]]));

    expect(screen.getByRole("columnheader", { name: "#" })).toBeTruthy();
    const numbers = screen.getAllByTestId("row-number").map(cell => cell.textContent);
    expect(numbers).toEqual(["1", "2", "3"]);
  });

  it("offers the rest of a capped result, and says nothing when there is no rest", () => {
    const fetchAll = vi.fn();
    draw(result([[1]], true), fetchAll);

    fireEvent.click(screen.getByRole("button", { name: /fetch all/i }));
    expect(fetchAll).toHaveBeenCalled();

    cleanup();
    draw(result([[1]], false), fetchAll);
    expect(screen.queryByRole("button", { name: /fetch all/i })).toBeNull();
  });
});
