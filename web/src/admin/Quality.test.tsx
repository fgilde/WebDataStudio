// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const qualityRules = vi.fn();
const saveQualityRule = vi.fn();
const deleteQualityRule = vi.fn();
const runQualityRules = vi.fn();

vi.mock("../api", () => ({
  qualityRules: (...args: unknown[]) => qualityRules(...args),
  saveQualityRule: (...args: unknown[]) => saveQualityRule(...args),
  deleteQualityRule: (...args: unknown[]) => deleteQualityRule(...args),
  runQualityRules: (...args: unknown[]) => runQualityRules(...args),
}));

const { Quality } = await import("./Quality");

const rule = (over: Record<string, unknown> = {}) => ({
  id: "r1", connectionId: "c1", schema: "public", table: "orders", column: "customer_id",
  kind: "NotNull", argument: null, message: null, enabled: true, ...over,
});

const draw = (onOpenInEditor?: (sql: string) => void) =>
  render(
    <MantineProvider>
      <Quality connectionId="c1" onOpenInEditor={onOpenInEditor} />
    </MantineProvider>);

describe("Quality", () => {
  beforeEach(() => {
    cleanup();
    qualityRules.mockReset().mockResolvedValue([]);
    saveQualityRule.mockReset().mockResolvedValue(rule());
    deleteQualityRule.mockReset().mockResolvedValue(undefined);
    runQualityRules.mockReset();
  });

  it("says there are no rules rather than showing an empty table", async () => {
    draw();

    await waitFor(() => expect(screen.getByText(/No rules yet/)).toBeTruthy());
    // Nothing to run means the button says so by being unavailable.
    expect(screen.getByRole("button", { name: "Run now" }).hasAttribute("disabled")).toBe(true);
  });

  it("only asks for an argument where the kind needs one", async () => {
    draw();

    await waitFor(() => expect(screen.getByText(/No rules yet/)).toBeTruthy());
    // "Has a value" needs nothing else said.
    expect(screen.queryByLabelText("Argument")).toBeNull();
  });

  it("saves a rule and reloads the list", async () => {
    qualityRules.mockResolvedValueOnce([]).mockResolvedValueOnce([rule()]);

    draw();

    await waitFor(() => expect(screen.getByText(/No rules yet/)).toBeTruthy());

    fireEvent.change(screen.getAllByLabelText("Table")[0], { target: { value: "orders" } });
    fireEvent.change(screen.getAllByLabelText("Column")[0], { target: { value: "customer_id" } });
    fireEvent.click(screen.getByRole("button", { name: /Add rule/ }));

    await waitFor(() => expect(saveQualityRule).toHaveBeenCalled());
    expect(saveQualityRule.mock.calls[0][1]).toMatchObject({
      connectionId: "c1", table: "orders", column: "customer_id", kind: "NotNull",
    });
    await waitFor(() => expect(screen.getByText("public.orders")).toBeTruthy());
  });

  it("cannot add a rule without a table", async () => {
    draw();

    await waitFor(() => expect(screen.getByText(/No rules yet/)).toBeTruthy());
    expect(screen.getByRole("button", { name: /Add rule/ }).hasAttribute("disabled")).toBe(true);
  });

  it("shows what each rule counted, and offers the statement behind a failure", async () => {
    qualityRules.mockResolvedValue([rule(), rule({ id: "r2", column: "total", kind: "Unique" })]);
    runQualityRules.mockResolvedValue({
      ran: 2, failing: 1,
      results: [
        {
          rule: rule(), violations: 12, statement: "SELECT count(*) FROM orders WHERE …",
          ranAt: "2026-08-28T10:00:00Z", error: null,
        },
        {
          rule: rule({ id: "r2", column: "total", kind: "Unique" }), violations: 0,
          statement: "…", ranAt: "2026-08-28T10:00:00Z", error: null,
        },
      ],
    });

    const opened: string[] = [];
    draw(sql => opened.push(sql));

    await waitFor(() => expect(screen.getAllByText("public.orders")).toHaveLength(2));
    fireEvent.click(screen.getByRole("button", { name: "Run now" }));

    await waitFor(() => expect(screen.getByText("12 rows")).toBeTruthy());
    expect(screen.getByText("ok")).toBeTruthy();
    expect(screen.getByText("1 failing")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Show" }));
    expect(opened).toEqual(["SELECT count(*) FROM orders WHERE …"]);
  });

  it("reports a rule that could not be checked without calling it a failure", async () => {
    qualityRules.mockResolvedValue([rule()]);
    runQualityRules.mockResolvedValue({
      ran: 1, failing: 1,
      results: [{
        rule: rule(), violations: 0, statement: "…", ranAt: "2026-08-28T10:00:00Z",
        error: "column \"nope\" does not exist",
      }],
    });

    draw();

    await waitFor(() => expect(screen.getByText("public.orders")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Run now" }));

    await waitFor(() => expect(screen.getByText(/does not exist/)).toBeTruthy());
    expect(screen.queryByText("everything passed")).toBeNull();
  });

  it("switching a rule off saves it rather than deleting it", async () => {
    qualityRules.mockResolvedValue([rule()]);

    draw();

    await waitFor(() => expect(screen.getByText("public.orders")).toBeTruthy());
    fireEvent.click(screen.getByLabelText("Enable orders.customer_id NotNull"));

    await waitFor(() => expect(saveQualityRule).toHaveBeenCalled());
    expect(saveQualityRule.mock.calls[0][1]).toMatchObject({ id: "r1", enabled: false });
    expect(deleteQualityRule).not.toHaveBeenCalled();
  });

  it("deletes a rule", async () => {
    qualityRules.mockResolvedValueOnce([rule()]).mockResolvedValueOnce([]);

    draw();

    await waitFor(() => expect(screen.getByText("public.orders")).toBeTruthy());
    fireEvent.click(screen.getByLabelText("Delete rule for orders"));

    await waitFor(() => expect(deleteQualityRule).toHaveBeenCalledWith("c1", "r1"));
    await waitFor(() => expect(screen.getByText(/No rules yet/)).toBeTruthy());
  });

  it("shows why a run failed", async () => {
    qualityRules.mockResolvedValue([rule()]);
    runQualityRules.mockRejectedValue(new Error("connection refused"));

    draw();

    await waitFor(() => expect(screen.getByText("public.orders")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Run now" }));

    await waitFor(() => expect(screen.getByText("connection refused")).toBeTruthy());
  });
});
