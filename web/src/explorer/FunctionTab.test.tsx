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

const functionInfo = vi.fn();
const functionTrialRun = vi.fn();

vi.mock("../api", () => ({
  functionInfo: (...args: unknown[]) => functionInfo(...args),
  functionTrialRun: (...args: unknown[]) => functionTrialRun(...args),
}));

const { FunctionTab } = await import("./FunctionTab");

const info = {
  supported: true,
  language: "plpgsql",
  returns: "numeric",
  returnsSet: false,
  source: "CREATE FUNCTION spent_by(...)",
  arguments: [
    { name: "p_country", type: "text", mode: "IN", hasDefault: true },
    { name: "p_year", type: "integer", mode: "IN", hasDefault: false },
  ],
};

const setup = () => {
  functionInfo.mockResolvedValue(info);
  functionTrialRun.mockResolvedValue({
    columns: ["spent_by"], rows: [[169.49]], notices: ["adding up orders for GB"],
    elapsedMs: 1.1, truncated: false,
  });

  render(
    <MantineProvider>
      <FunctionTab connectionId="c1" objectRef="Function:public/spent_by" />
    </MantineProvider>,
  );
};

describe("FunctionTab", () => {
  beforeEach(() => { cleanup(); functionInfo.mockReset(); functionTrialRun.mockReset(); });

  it("keeps what is typed into an argument, one keystroke after another", async () => {
    setup();
    await waitFor(() => expect(screen.getByLabelText(/p_country/)).toBeTruthy());

    // Reading the event inside the state updater used to throw on the second change and take the
    // whole studio with it.
    fireEvent.change(screen.getByLabelText(/p_country/), { target: { value: "PT" } });
    fireEvent.change(screen.getByLabelText(/p_year/), { target: { value: "2026" } });
    fireEvent.change(screen.getByLabelText(/p_country/), { target: { value: "GB" } });

    expect((screen.getByLabelText(/p_country/) as HTMLInputElement).value).toBe("GB");
    expect((screen.getByLabelText(/p_year/) as HTMLInputElement).value).toBe("2026");
  });

  it("leaves a trailing empty argument out, so the function's own default applies", async () => {
    setup();
    await waitFor(() => expect(screen.getByLabelText(/p_country/)).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: /Run and roll back/ }));

    // Both empty: the second has no default, so it goes as NULL; the first is dropped entirely.
    await waitFor(() => expect(functionTrialRun).toHaveBeenCalled());
    expect(functionTrialRun.mock.calls[0][2]).toEqual([null, null]);
  });

  it("passes what was typed, in order", async () => {
    setup();
    await waitFor(() => expect(screen.getByLabelText(/p_country/)).toBeTruthy());

    fireEvent.change(screen.getByLabelText(/p_country/), { target: { value: "PT" } });
    fireEvent.click(screen.getByRole("button", { name: /Run and roll back/ }));

    await waitFor(() => expect(functionTrialRun).toHaveBeenCalled());
    expect(functionTrialRun.mock.calls[0][2]).toEqual(["PT", null]);
  });
});
