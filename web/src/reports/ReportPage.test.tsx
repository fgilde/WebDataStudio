// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { MemoryRouter, Route, Routes } from "react-router-dom";

const listReports = vi.fn();
const runReport = vi.fn();

vi.mock("../api", () => ({
  listReports: (...args: unknown[]) => listReports(...args),
  runReport: (...args: unknown[]) => runReport(...args),
}));

const { ReportPage } = await import("./ReportPage");

const report = {
  id: "q7", name: "Orders by month", folder: "Sales", connectionId: "pg",
  parameters: ["from", "to"],
  sql: "SELECT month, count(*) FROM orders WHERE placed BETWEEN :from AND :to GROUP BY month",
};

const result = {
  name: "Orders by month",
  columns: [{ name: "month", dataType: "text" }, { name: "orders", dataType: "bigint" }],
  rows: [["2026-06", 12], ["2026-07", 19]] as (string | number | null)[][],
  truncated: false,
};

const draw = (path: string) => render(
  <MantineProvider>
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/report" element={<ReportPage />} />
        <Route path="/report/:id" element={<ReportPage />} />
      </Routes>
    </MemoryRouter>
  </MantineProvider>);

describe("ReportPage", () => {
  beforeEach(() => {
    cleanup();
    listReports.mockReset().mockResolvedValue([report]);
    runReport.mockReset().mockResolvedValue(result);
  });

  it("lists the reports there are, with what each one asks for", async () => {
    draw("/report");

    await waitFor(() => expect(screen.getByText(/Sales \/ Orders by month/)).toBeTruthy());
    expect(screen.getByText(/asks for from, to/)).toBeTruthy();
  });

  it("asks for the parameters and runs with them", async () => {
    draw("/report/q7");

    await waitFor(() => expect(screen.getByText("Orders by month")).toBeTruthy());

    fireEvent.change(screen.getAllByLabelText("from")[0], { target: { value: "2026-06-01" } });
    fireEvent.change(screen.getAllByLabelText("to")[0], { target: { value: "2026-07-31" } });
    fireEvent.click(screen.getByRole("button", { name: "Run" }));

    await waitFor(() => expect(runReport).toHaveBeenCalled());
    expect(runReport.mock.calls[0]).toEqual(["q7", { from: "2026-06-01", to: "2026-07-31" }]);

    await waitFor(() => expect(screen.getByText("2026-07")).toBeTruthy());
    expect(screen.getByText("2 row(s)")).toBeTruthy();
  });

  it("a link with every value in it runs by itself", async () => {
    // That is the whole point of the link: "the numbers for last month" is something to send.
    draw("/report/q7?from=2026-06-01&to=2026-06-30");

    await waitFor(() => expect(runReport).toHaveBeenCalled());
    expect(runReport.mock.calls[0][1]).toEqual({ from: "2026-06-01", to: "2026-06-30" });
    await waitFor(() => expect(screen.getByText("2026-06")).toBeTruthy());
  });

  it("and a link with a value missing waits for it", async () => {
    draw("/report/q7?from=2026-06-01");

    await waitFor(() => expect(screen.getByText("Orders by month")).toBeTruthy());
    expect(runReport).not.toHaveBeenCalled();
    expect((screen.getAllByLabelText("from")[0] as HTMLInputElement).value).toBe("2026-06-01");
  });

  it("shows why a report could not run", async () => {
    runReport.mockRejectedValue(new Error("this report needs from, to"));

    draw("/report/q7");

    await waitFor(() => expect(screen.getByText("Orders by month")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Run" }));

    await waitFor(() => expect(screen.getByText("this report needs from, to")).toBeTruthy());
  });

  it("says when there is nothing saved to run", async () => {
    listReports.mockResolvedValue([]);

    draw("/report");

    await waitFor(() => expect(screen.getByText(/None yet/)).toBeTruthy());
  });
});
