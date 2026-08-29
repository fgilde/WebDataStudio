// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { cleanup, render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { HealthDot } from "./HealthDot";

const checkConnectionHealth = vi.fn();

vi.mock("../api", () => ({
  checkConnectionHealth: (...args: unknown[]) => checkConnectionHealth(...args),
}));

const show = (auto: boolean) => render(
  <MantineProvider><HealthDot id="c1" auto={auto} /></MantineProvider>);

const dot = () => screen.getByRole("button");

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  checkConnectionHealth.mockResolvedValue({ ok: true, milliseconds: 12, message: "connected" });
});

describe("the connection health dot", () => {
  it("asks nothing until the connection is expanded", () => {
    show(false);

    expect(checkConnectionHealth).not.toHaveBeenCalled();
    expect(dot().getAttribute("aria-label")).toContain("not checked yet");
  });

  it("checks once when the connection is expanded", async () => {
    show(true);

    await waitFor(() => expect(dot().getAttribute("aria-label")).toContain("answered in 12 ms"));
    expect(checkConnectionHealth).toHaveBeenCalledTimes(1);
  });

  it("checks again when the dot is clicked", async () => {
    show(false);
    fireEvent.click(dot());

    await waitFor(() => expect(checkConnectionHealth).toHaveBeenCalledTimes(1));
  });

  it("says what a server that is down said, rather than a red dot with no reason", async () => {
    checkConnectionHealth.mockResolvedValue({
      ok: false, milliseconds: 3000, message: "No connection could be made",
    });

    show(true);

    await waitFor(() =>
      expect(dot().getAttribute("aria-label")).toContain("No connection could be made"));
  });

  it("does not let a click on the dot open the connection as well", () => {
    const onClick = vi.fn();

    render(
      <MantineProvider>
        <div onClick={onClick}><HealthDot id="c1" auto={false} /></div>
      </MantineProvider>);

    fireEvent.click(screen.getByRole("button"));
    expect(onClick).not.toHaveBeenCalled();
  });
});
