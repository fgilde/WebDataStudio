// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";

const auditTrail = vi.fn();
vi.mock("../api", () => ({ auditTrail: (...args: unknown[]) => auditTrail(...args) }));

const { Audit } = await import("./Audit");

const entry = (over: Record<string, unknown> = {}) => ({
  id: 1, at: "2026-08-28T09:00:00Z", user: "ada", role: "admin", connectionId: "pg",
  action: "POST query/execute", detail: "DELETE FROM orders", status: 200, elapsedMs: 12,
  address: "::1", ...over,
});

const draw = (connectionId?: string) =>
  render(<MantineProvider><Audit connectionId={connectionId} /></MantineProvider>);

describe("Audit", () => {
  beforeEach(() => {
    cleanup();
    auditTrail.mockReset().mockResolvedValue({ enabled: true, entries: [] });
  });

  it("shows who did what, against which connection", async () => {
    auditTrail.mockResolvedValue({ enabled: true, entries: [entry()] });

    draw();

    await waitFor(() => expect(screen.getByText("POST query/execute")).toBeTruthy());
    expect(screen.getByText("DELETE FROM orders")).toBeTruthy();
    expect(screen.getByText("pg")).toBeTruthy();
    expect(screen.getByText("(admin)")).toBeTruthy();
  });

  it("starts filtered to the connection it was opened for", async () => {
    draw("pg");

    await waitFor(() => expect(auditTrail).toHaveBeenCalled());
    expect(auditTrail.mock.calls[0][0]).toMatchObject({ conn: "pg" });
  });

  it("asks again when the filters change", async () => {
    draw();

    await waitFor(() => expect(auditTrail).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getAllByLabelText("Who")[0], { target: { value: "grace" } });

    await waitFor(() =>
      expect(auditTrail.mock.calls.at(-1)?.[0]).toMatchObject({ user: "grace" }));
  });

  it("marks a refused request differently from one that worked", async () => {
    auditTrail.mockResolvedValue({
      enabled: true,
      entries: [entry(), entry({ id: 2, status: 403, user: "anonymous", role: "" })],
    });

    draw();

    await waitFor(() => expect(screen.getByText("403")).toBeTruthy());
    expect(screen.getByText("200")).toBeTruthy();
    // Nobody signed in has no role to show in brackets.
    expect(screen.getAllByText("(admin)")).toHaveLength(1);
  });

  it("says when the deployment turned the trail off", async () => {
    auditTrail.mockResolvedValue({ enabled: false, entries: [] });

    draw();

    await waitFor(() => expect(screen.getByText(/turned off/)).toBeTruthy());
  });

  it("says nothing has happened yet rather than showing an empty table", async () => {
    draw();

    await waitFor(() => expect(screen.getByText(/Nothing yet/)).toBeTruthy());
  });

  it("shows why the trail could not be read", async () => {
    auditTrail.mockRejectedValue(new Error("this needs the admin role"));

    draw();

    await waitFor(() => expect(screen.getByText("this needs the admin role")).toBeTruthy());
  });
});
