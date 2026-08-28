// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { cleanup, render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { SchemaDrift } from "./SchemaDrift";

const schemaDrift = vi.fn();
const driftScript = vi.fn();
const takeSnapshot = vi.fn();

vi.mock("../api", () => ({
  schemaDrift: (...args: unknown[]) => schemaDrift(...args),
  driftScript: (...args: unknown[]) => driftScript(...args),
  takeSnapshot: (...args: unknown[]) => takeSnapshot(...args),
}));

const wrap = (onOpenInEditor?: (sql: string) => void) =>
  render(
    <MantineProvider>
      <SchemaDrift connectionId="c1" onOpenInEditor={onOpenInEditor} />
    </MantineProvider>);

describe("what moved since the snapshot", () => {
  beforeEach(() => {
    cleanup();
    schemaDrift.mockReset();
    driftScript.mockReset();
    takeSnapshot.mockReset();

    schemaDrift.mockResolvedValue({
      configured: true,
      drift: {
        before: "2026-08-25T08:00:00Z", after: "2026-08-29T08:00:00Z",
        summary: "1 added, 1 changed",
        added: ["Table:public/orders"], removed: [], changed: ["people: column now: city text null"],
      },
    });
    driftScript.mockResolvedValue({
      before: "2026-08-25T08:00:00Z",
      script: 'CREATE TABLE "public"."orders" ();',
      destructive: false,
      needsAPerson: ["people.total changed type or nullability — check the data first"],
      statements: 1,
    });
  });

  it("lists what was added, removed and changed", async () => {
    wrap();

    expect(await screen.findByText("Table:public/orders")).toBeTruthy();
    expect(screen.getByText(/column now: city/)).toBeTruthy();
    expect(screen.getByText("added")).toBeTruthy();
  });

  /// The whole point: the script goes into a query tab, where a person reads it before it runs.
  it("hands the script to a query tab, with what it would not write as comments", async () => {
    const opened = vi.fn();
    wrap(opened);

    await waitFor(() => expect(screen.getByRole("button", { name: "Script the difference…" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Script the difference…" }));

    await waitFor(() => expect(opened).toHaveBeenCalled());

    const sql = opened.mock.calls[0][0] as string;
    expect(sql).toContain("-- people.total changed type or nullability");
    expect(sql).toContain("CREATE TABLE");
  });

  it("says which setting is missing rather than showing an empty panel", async () => {
    schemaDrift.mockResolvedValue({ configured: false, drift: null });
    wrap();

    expect(await screen.findByText(/WDS_SCHEMA_SNAPSHOT_DIR/)).toBeTruthy();
  });

  it("says the schema is where it was when nothing moved", async () => {
    schemaDrift.mockResolvedValue({
      configured: true,
      drift: { before: null, after: "2026-08-29T08:00:00Z", summary: "", added: [], removed: [], changed: [] },
    });

    wrap();

    expect(await screen.findByText(/where the snapshot left it/)).toBeTruthy();
  });

  it("takes one on the button", async () => {
    takeSnapshot.mockResolvedValue({ moved: 1 });
    wrap();

    await waitFor(() => expect(screen.getByRole("button", { name: "Snapshot now" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Snapshot now" }));

    await waitFor(() => expect(takeSnapshot).toHaveBeenCalled());
  });
});
