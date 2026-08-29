// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { RowHistoryModal } from "./RowHistoryModal";

const rowHistory = vi.fn();
vi.mock("../api", () => ({ rowHistory: (...args: unknown[]) => rowHistory(...args) }));

const column = (name: string) => ({ name, dataType: "text", nullable: true });

const wrap = (keyValues: Record<string, string> | null = { id: "1" }) =>
  render(
    <MantineProvider>
      <RowHistoryModal connectionId="c1" objectRef="Table:dbo/customers" keyValues={keyValues}
        label="customers" onClose={() => {}} />
    </MantineProvider>);

describe("what a row looked like before", () => {
  beforeEach(() => {
    cleanup();
    rowHistory.mockReset();

    rowHistory.mockResolvedValue({
      supported: true,
      columns: [column("id"), column("name"), column("city")],
      versions: [
        { from: "2026-08-29", to: null, values: [1, "ada l", "oxford"], changed: ["name"] },
        { from: "2026-08-20", to: "2026-08-29", values: [1, "ada", "oxford"], changed: ["city"] },
        { from: "2026-08-01", to: "2026-08-20", values: [1, "ada", "london"], changed: [] },
      ],
      note: null,
    });
  });

  it("lists the versions newest first, and marks the current one", async () => {
    wrap();

    expect(await screen.findByText("ada l")).toBeTruthy();
    expect(screen.getByText("now")).toBeTruthy();
    expect(screen.getByText("london")).toBeTruthy();
  });

  it("asks for exactly the row it was opened on", async () => {
    wrap({ id: "42" });

    await waitFor(() => expect(rowHistory).toHaveBeenCalledWith(
      "c1", "Table:dbo/customers", { id: "42" }));
  });

  /// An engine that keeps no history says so rather than showing an empty table somebody stares at.
  it("passes the database's own answer through", async () => {
    rowHistory.mockResolvedValue({
      supported: false, columns: [], versions: [],
      note: "this table is not system-versioned, so the database kept no history of it",
    });

    wrap();

    expect(await screen.findByText(/not system-versioned/)).toBeTruthy();
  });

  it("is not open until a row was picked", () => {
    wrap(null);

    expect(screen.queryByText("History of customers")).toBeNull();
    expect(rowHistory).not.toHaveBeenCalled();
  });
});
