// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const importFileAsTable = vi.fn();
const listConnections = vi.fn();

vi.mock("../api", () => ({
  importFileAsTable: (...args: unknown[]) => importFileAsTable(...args),
  listConnections: () => listConnections(),
}));

const { NewTableDialog } = await import("./NewTableDialog");

const plan = {
  schema: "public", table: "people",
  columns: [
    { name: "id", sourceType: "BIGINT", targetType: "BIGINT" },
    { name: "born", sourceType: "DATE", targetType: "DATE" },
  ],
  createSql: "CREATE TABLE \"public\".\"people\" (\n  \"id\" BIGINT NULL,\n  \"born\" DATE NULL\n)",
  rows: 3,
  preview: [["1", "1815-12-10"]],
};

const upload = (onDone?: (table: string) => void) => render(
  <MantineProvider>
    <NewTableDialog connectionId="c1" onClose={() => {}} onDone={onDone} />
  </MantineProvider>,
);

const fromBucket = () => render(
  <MantineProvider>
    <NewTableDialog connectionId="" onClose={() => {}}
      source={{ storageConnection: "lake", objectRef: "StorageObject:lake/people.parquet",
        name: "people.parquet" }} />
  </MantineProvider>,
);

const pickFile = (name = "people.csv") => {
  const input = document.querySelector("input[type=file]") as HTMLInputElement;
  const file = new File(["id,name\n1,ada\n"], name, { type: "text/csv" });

  Object.defineProperty(input, "files", { value: [file], configurable: true });
  fireEvent.change(input);
  return file;
};

describe("NewTableDialog", () => {
  beforeEach(() => {
    cleanup();
    importFileAsTable.mockReset();
    listConnections.mockReset();
    listConnections.mockResolvedValue([
      { id: "pg", name: "SHOP", engine: "postgresql" },
      { id: "lake", name: "LAKE", engine: "storage" },
    ]);
  });

  it("proposes a table name from the file rather than asking for one twice", () => {
    upload();
    pickFile("people-2026.csv");

    expect(screen.getByLabelText("New table")).toHaveProperty("value", "people_2026");
  });

  it("reads the file first and creates nothing until the plan has been shown", async () => {
    importFileAsTable.mockResolvedValue(plan);

    upload();
    pickFile();

    expect(screen.getByRole("button", { name: "Create and load" })).toHaveProperty("disabled", true);

    fireEvent.click(screen.getByRole("button", { name: "Read the file" }));

    await waitFor(() => expect(screen.getAllByText("BIGINT").length).toBeGreaterThan(0));
    expect(importFileAsTable).toHaveBeenCalledWith("c1", expect.objectContaining({ apply: false }));
    expect(screen.getByText(/CREATE TABLE/)).toBeTruthy();
    expect(screen.getByText("3 rows")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Create and load" })).toHaveProperty("disabled", false);
  });

  it("then creates and loads it, and says which table", async () => {
    importFileAsTable.mockResolvedValueOnce(plan)
      .mockResolvedValueOnce({ table: "public.people", rows: 3, createSql: plan.createSql });
    const onDone = vi.fn();

    upload(onDone);
    pickFile();
    fireEvent.click(screen.getByRole("button", { name: "Read the file" }));
    await waitFor(() => expect(screen.getAllByText("BIGINT").length).toBeGreaterThan(0));

    fireEvent.click(screen.getByRole("button", { name: "Create and load" }));

    await waitFor(() => expect(onDone).toHaveBeenCalledWith("public.people"));
    expect(importFileAsTable).toHaveBeenLastCalledWith("c1", expect.objectContaining({ apply: true }));
  });

  it("asks which connection the table goes into when the file is in a bucket", async () => {
    fromBucket();

    await waitFor(() => expect(screen.getByText("Into this connection")).toBeTruthy());
    // A bucket is not one of the answers: there is no table to create in one.
    expect(screen.getByText(/Reading/)).toBeTruthy();
    // The extension is not part of the name: people.parquet becomes people.
    expect(screen.getByLabelText("New table")).toHaveProperty("value", "people");
  });

  it("shows what went wrong instead of a silent dialog", async () => {
    importFileAsTable.mockRejectedValue(new Error("nothing here reads that file"));

    upload();
    pickFile("notes.zip");
    fireEvent.click(screen.getByRole("button", { name: "Read the file" }));

    await waitFor(() => expect(screen.getByText("nothing here reads that file")).toBeTruthy());
  });
});
