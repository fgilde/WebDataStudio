// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

window.matchMedia ??= ((query: string) => ({
  matches: false, media: query, onchange: null,
  addListener: () => {}, removeListener: () => {},
  addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
})) as typeof window.matchMedia;

globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

const searchData = vi.fn();
vi.mock("../api", () => ({ searchData: (...args: unknown[]) => searchData(...args) }));

const { DataSearchPanel } = await import("./DataSearchPanel");

const found = {
  hits: [
    { schema: "public", table: "orders", column: "reference", dataType: "text", matches: 3 },
    { schema: "public", table: "people", column: "id", dataType: "integer", matches: 1 },
  ],
  tablesSearched: 12, tablesSkipped: 2, notes: ["public.pictures: no column can hold it"],
  truncated: false,
};

const draw = (onOpen?: (t: string, s: string, c: string, v: string) => void) => render(
  <MantineProvider>
    <DataSearchPanel connectionId="c1" schema="public" onOpen={onOpen} />
  </MantineProvider>,
);

const type = (value: string) => {
  fireEvent.change(screen.getByLabelText("Find this value"), { target: { value } });
};

describe("DataSearchPanel", () => {
  beforeEach(() => {
    cleanup();
    searchData.mockReset();
  });

  it("says where the value is, in which column, and how many rows", async () => {
    searchData.mockResolvedValue(found);

    draw();
    type("4711");
    fireEvent.click(screen.getByRole("button", { name: "Search" }));

    await waitFor(() => expect(screen.getByText("public.orders")).toBeTruthy());
    expect(screen.getByText("reference")).toBeTruthy();
    expect(screen.getByText("3 rows")).toBeTruthy();
    expect(screen.getByText(/12 tables searched, 2 skipped/)).toBeTruthy();
    expect(searchData).toHaveBeenCalledWith("c1", "4711", { schema: "public", exact: false });
  });

  it("searches on Enter as well, because that is what a search box does", async () => {
    searchData.mockResolvedValue(found);

    draw();
    type("4711");
    fireEvent.keyDown(screen.getByLabelText("Find this value"), { key: "Enter" });

    await waitFor(() => expect(searchData).toHaveBeenCalled());
  });

  it("passes the whole-value switch through", async () => {
    searchData.mockResolvedValue(found);

    draw();
    type("ORD-4711");
    fireEvent.click(screen.getByLabelText("whole value"));
    fireEvent.click(screen.getByRole("button", { name: "Search" }));

    await waitFor(() => expect(searchData)
      .toHaveBeenCalledWith("c1", "ORD-4711", { schema: "public", exact: true }));
  });

  it("opens the table a hit is in, filtered on the column that matched", async () => {
    searchData.mockResolvedValue(found);
    const onOpen = vi.fn();

    draw(onOpen);
    type("4711");
    fireEvent.click(screen.getByRole("button", { name: "Search" }));

    await waitFor(() => expect(screen.getByText("public.orders")).toBeTruthy());
    fireEvent.click(screen.getByText("public.orders"));

    expect(onOpen).toHaveBeenCalledWith("orders", "public", "reference", "4711");
  });

  it("says when nothing could hold the value rather than showing an empty table", async () => {
    searchData.mockResolvedValue({ ...found, hits: [] });

    draw();
    type("nothing");
    fireEvent.click(screen.getByRole("button", { name: "Search" }));

    await waitFor(() => expect(screen.getByText(/Not in any column that could hold it/)).toBeTruthy());
  });

  it("says when it stopped at the table limit", async () => {
    searchData.mockResolvedValue({ ...found, truncated: true });

    draw();
    type("4711");
    fireEvent.click(screen.getByRole("button", { name: "Search" }));

    await waitFor(() => expect(screen.getByText("stopped at the table limit")).toBeTruthy());
  });
});
