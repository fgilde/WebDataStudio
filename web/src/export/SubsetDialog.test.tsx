// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const buildSubset = vi.fn();
vi.mock("../api", () => ({ buildSubset: (...args: unknown[]) => buildSubset(...args) }));

const { SubsetDialog } = await import("./SubsetDialog");

const target = { connectionId: "pg", schema: "public", table: "orders" };

const draw = (onOpenInEditor?: (sql: string) => void) =>
  render(
    <MantineProvider>
      <SubsetDialog target={target} onClose={() => {}} onOpenInEditor={onOpenInEditor} />
    </MantineProvider>);

const result = {
  script: "-- A subset of orders\nINSERT INTO \"public\".\"orders\" (\"id\") VALUES\n  (1);",
  tables: [
    { schema: "public", name: "customers", rows: 3, statement: "SELECT …" },
    { schema: "public", name: "orders", rows: 10, statement: "SELECT …" },
  ],
  rows: 13,
  notes: [] as string[],
};

describe("SubsetDialog", () => {
  beforeEach(() => {
    cleanup();
    buildSubset.mockReset().mockResolvedValue(result);
  });

  it("asks for the size and the depth, and builds with them", async () => {
    draw();

    fireEvent.change(screen.getAllByLabelText("Rows")[0], { target: { value: "50" } });
    fireEvent.change(screen.getAllByLabelText("Where")[0],
      { target: { value: "placed > '2026-01-01'" } });
    fireEvent.click(screen.getByRole("button", { name: /Build the subset/ }));

    await waitFor(() => expect(buildSubset).toHaveBeenCalled());
    expect(buildSubset.mock.calls[0]).toEqual(["pg", {
      table: "orders", schema: "public", where: "placed > '2026-01-01'",
      rows: 50, depth: 4, anonymise: true, includeSchema: true,
    }]);
  });

  it("shows which tables came along and how many rows", async () => {
    draw();

    fireEvent.click(screen.getByRole("button", { name: /Build the subset/ }));

    await waitFor(() => expect(screen.getByText("public.customers")).toBeTruthy());
    expect(screen.getByText("public.orders")).toBeTruthy();
    expect(screen.getByText("13 rows")).toBeTruthy();
    expect(screen.getByText("2 tables")).toBeTruthy();
  });

  it("warns before building a copy of real data", () => {
    draw();

    expect(screen.queryByText(/belongs wherever real data belongs/)).toBeNull();
    fireEvent.click(screen.getByLabelText("Replace what is about people"));

    expect(screen.getByText(/belongs wherever real data belongs/)).toBeTruthy();
  });

  it("hands the script to a query tab", async () => {
    const opened: string[] = [];
    draw(sql => opened.push(sql));

    fireEvent.click(screen.getByRole("button", { name: /Build the subset/ }));
    await waitFor(() => expect(screen.getByText("13 rows")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Open in editor" }));
    expect(opened).toEqual([result.script]);
  });

  it("passes on what the subset could not do", async () => {
    buildSubset.mockResolvedValue({
      ...result,
      notes: ["orders.fk_two_columns is a multi-column foreign key and was left out"],
    });

    draw();
    fireEvent.click(screen.getByRole("button", { name: /Build the subset/ }));

    await waitFor(() => expect(screen.getByText(/multi-column foreign key/)).toBeTruthy());
  });

  it("shows why a subset could not be built", async () => {
    buildSubset.mockRejectedValue(new Error("there is no table called orders here"));

    draw();
    fireEvent.click(screen.getByRole("button", { name: /Build the subset/ }));

    await waitFor(() =>
      expect(screen.getByText("there is no table called orders here")).toBeTruthy());
  });
});
