// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const analyzeQuery = vi.fn();
const tryIndex = vi.fn();

vi.mock("../api", () => ({
  analyzeQuery: (...args: unknown[]) => analyzeQuery(...args),
  tryIndex: (...args: unknown[]) => tryIndex(...args),
  applyScript: vi.fn(),
  previewScript: vi.fn(),
}));

const { PlanPanel } = await import("./PlanPanel");

const node = (operation: string, cost: number) => ({
  operation, detail: null, estimatedCost: cost, estimatedRows: 100, actualRows: null,
  actualMs: null, children: [], warnings: [],
});

const result = {
  plan: node("Seq Scan", 1000),
  findings: [
    {
      id: "missing-index", severity: "warning", title: "Index suggestion for orders",
      detail: "orders.person_id is filtered on and not indexed",
      statement: "CREATE INDEX ix_orders_person ON orders (person_id)",
    },
    {
      id: "bloat", severity: "info", title: "Bloat", detail: "20% bloat",
      statement: "VACUUM (ANALYZE) orders",
    },
  ],
};

const draw = () => render(
  <MantineProvider>
    <PlanPanel connectionId="pg" sql="SELECT * FROM orders WHERE person_id = 1" />
  </MantineProvider>);

/// The panel explains on request rather than on mount — opening a tab must not run anything — so
/// every case starts by asking for the plan.
const explain = async () => {
  draw();
  fireEvent.click(screen.getByLabelText("Explain"));
  await waitFor(() => expect(screen.getByRole("tab", { name: /Findings/i })).toBeTruthy());
  fireEvent.click(screen.getByRole("tab", { name: /Findings/i }));
  await waitFor(() => expect(screen.getByText("Index suggestion for orders")).toBeTruthy());
};

describe("PlanPanel", () => {
  beforeEach(() => {
    cleanup();
    analyzeQuery.mockReset().mockResolvedValue(result);
    tryIndex.mockReset();
  });

  it("offers a trial for a suggested index and for nothing else", async () => {
    await explain();

    // A VACUUM is not something the studio can create and drop again, so it gets no trial.
    expect(screen.getAllByRole("button", { name: "Try it" })).toHaveLength(1);
  });

  it("says what the index did to the plan", async () => {
    tryIndex.mockResolvedValue({
      index: "wds_trial_ab12", created: "CREATE INDEX wds_trial_ab12 ON orders (person_id)",
      before: node("Seq Scan", 1000), after: node("Index Scan", 40),
      costBefore: 1000, costAfter: 40,
      verdict: "cheaper by 96%, and it stopped scanning the table", leftBehind: null,
    });

    await explain();
    fireEvent.click(screen.getByRole("button", { name: "Try it" }));

    await waitFor(() =>
      expect(screen.getByText("cheaper by 96%, and it stopped scanning the table")).toBeTruthy());

    expect(screen.getByText(/Seq Scan → Index Scan, cost 1000 → 40/)).toBeTruthy();
    // Said plainly: the studio created something on a real table and took it away again.
    expect(screen.getByText(/created as wds_trial_ab12 and dropped again/)).toBeTruthy();

    expect(tryIndex.mock.calls[0]).toEqual([
      "pg", "SELECT * FROM orders WHERE person_id = 1",
      "CREATE INDEX ix_orders_person ON orders (person_id)",
    ]);
  });

  it("and says when it could not take it away again", async () => {
    tryIndex.mockResolvedValue({
      index: "wds_trial_ab12", created: "CREATE INDEX …",
      before: node("Seq Scan", 1000), after: node("Index Scan", 40),
      costBefore: 1000, costAfter: 40, verdict: "cheaper by 96%",
      leftBehind: "wds_trial_ab12 could not be dropped (permission denied)",
    });

    await explain();
    fireEvent.click(screen.getByRole("button", { name: "Try it" }));

    await waitFor(() => expect(screen.getByText(/could not be dropped/)).toBeTruthy());
  });
});
