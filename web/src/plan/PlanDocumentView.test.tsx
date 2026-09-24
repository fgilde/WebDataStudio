// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import type { PlanDocumentDto } from "../api";

const save = vi.fn();
vi.mock("./planModel", async original => ({ ...(await original<typeof import("./planModel")>()), savePlanFile: (...a: unknown[]) => save(...a) }));

const { PlanDocumentView } = await import("./PlanDocumentView");

const doc = (rawFormat: string | null): PlanDocumentDto => ({
  engine: "sqlserver", raw: "<ShowPlanXML/>", rawFormat,
  statements: [
    { text: "DECLARE @x int", type: "DECLARE", cost: null, properties: [], warnings: [], missingIndexes: [], root: null },
    {
      text: "SELECT 1", type: "SELECT", cost: 1, warnings: ["Plan Affecting Convert: x"],
      missingIndexes: ["CREATE INDEX [IX] ON [dbo].[T] ([A]);"],
      properties: [{ name: "Memory Grant", value: "1024", children: [] }],
      root: { operation: "Table Scan", detail: null, estimatedCost: 1, estimatedRows: 5, actualRows: null, actualMs: null, children: [], warnings: [] },
    },
  ],
});

const draw = (d: PlanDocumentDto) => render(<MantineProvider><PlanDocumentView document={d} name="q" /></MantineProvider>);

describe("PlanDocumentView", () => {
  beforeEach(() => cleanup());

  it("opens on the first statement that has a plan, with its header, warnings and missing index", () => {
    draw(doc("sqlplan"));
    expect(screen.getByText("Memory Grant 1024")).toBeTruthy();
    expect(screen.getByText(/Plan Affecting Convert/)).toBeTruthy();
    expect(screen.getByText("CREATE INDEX [IX] ON [dbo].[T] ([A]);")).toBeTruthy();
    // One statement with a plan: no picker.
    expect(screen.queryByLabelText("Statement")).toBeNull();
  });

  it("saves the raw plan as .sqlplan", async () => {
    draw(doc("sqlplan"));
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));
    fireEvent.click(await screen.findByText("Save as .sqlplan"));
    expect(save).toHaveBeenCalledWith("<ShowPlanXML/>", "q", "sqlplan");
  });

  it("offers no export for a plan without its own format", () => {
    draw(doc(null));
    expect(screen.queryByRole("button", { name: /Save/ })).toBeNull();
  });

  it("folds a long list of warnings behind a count, each warning once", () => {
    const many = doc("sqlplan");
    many.statements[1].warnings = [...Array(6)].map((_, i) => `Plan Affecting Convert: ${i % 3}`);
    draw(many);

    // Six warnings, three distinct: counted, and not in the way until asked for.
    fireEvent.click(screen.getByRole("button", { name: /3 warnings/ }));
    expect(screen.getAllByText(/Plan Affecting Convert: \d/)).toHaveLength(3);
  });
});
