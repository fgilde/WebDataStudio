// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { cleanup, render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { KeepAsTableDialog } from "./KeepAsTableButton";

const planResultTable = vi.fn();
const keepResultAsTable = vi.fn();
const listConnections = vi.fn();

vi.mock("../api", () => ({
  planResultTable: (...args: unknown[]) => planResultTable(...args),
  keepResultAsTable: (...args: unknown[]) => keepResultAsTable(...args),
  listConnections: (...args: unknown[]) => listConnections(...args),
}));

vi.mock("../shell/jobs", () => ({
  runJob: (_meta: unknown, work: () => Promise<unknown>) => work(),
}));

const plan = (exactTypes: boolean) => ({
  schema: "", table: "t", createSql: "CREATE TABLE t (id BIGINT)", exactTypes,
  columns: [{ name: "id", sourceType: "int", targetType: "BIGINT" }],
});

const show = () => render(
  <MantineProvider>
    <KeepAsTableDialog connectionId="c1" sql="SELECT 1" onClose={() => {}} />
  </MantineProvider>);

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  listConnections.mockResolvedValue([{ id: "c1", name: "SHOP" }, { id: "c2", name: "WAREHOUSE" }]);
  planResultTable.mockResolvedValue(plan(true));
  keepResultAsTable.mockResolvedValue({ table: "t", rows: 3, createSql: "" });
});

describe("keep a result as a table", () => {
  it("shows the CREATE TABLE before anything is created", async () => {
    show();

    await waitFor(() => expect(screen.getByText(/CREATE TABLE t/)).toBeTruthy());
    expect(keepResultAsTable).not.toHaveBeenCalled();
  });

  it("says when the types are the source's own and when they are approximated", async () => {
    show();
    await waitFor(() => expect(screen.getByText(/keep the types they already have/)).toBeTruthy());

    cleanup();
    planResultTable.mockResolvedValue(plan(false));
    show();

    await waitFor(() => expect(screen.getByText(/nearest type/)).toBeTruthy());
  });

  it("will not create a table with no name", async () => {
    show();
    await waitFor(() => expect(screen.getByText(/CREATE TABLE t/)).toBeTruthy());

    const button = screen.getByRole("button", { name: /Create it and fill it/ });
    expect(button.hasAttribute("disabled")).toBe(true);
  });

  it("sends the name, the schema and the target connection", async () => {
    show();
    await waitFor(() => expect(screen.getByText(/CREATE TABLE t/)).toBeTruthy());

    fireEvent.change(screen.getByLabelText("Table name"), { target: { value: "orders_by_month" } });
    fireEvent.change(screen.getByLabelText("Schema"), { target: { value: "reporting" } });

    await waitFor(() =>
      expect(screen.getByRole("button", { name: /Create it and fill it/ }).hasAttribute("disabled"))
        .toBe(false));

    fireEvent.click(screen.getByRole("button", { name: /Create it and fill it/ }));

    await waitFor(() => expect(keepResultAsTable).toHaveBeenCalledWith({
      connectionId: "c1",
      sql: "SELECT 1",
      table: "orders_by_month",
      schema: "reporting",
      targetConnectionId: "c1",
    }));
  });

  it("reports what the server refused rather than swallowing it", async () => {
    planResultTable.mockRejectedValue(new Error("this connection is read-only"));
    show();

    await waitFor(() => expect(screen.getByText("this connection is read-only")).toBeTruthy());
  });
});
