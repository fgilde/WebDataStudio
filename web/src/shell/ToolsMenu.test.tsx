// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { emit, onShell, publishShell, resetShell } from "./bus";
import { ToolsMenu } from "./ToolsMenu";

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

const draw = () => render(<MantineProvider><ToolsMenu /></MantineProvider>);

const open = async () => {
  fireEvent.click(screen.getByRole("button", { name: "Tools" }));
  await waitFor(() => expect(screen.getByText("ER diagram")).toBeTruthy());
};

describe("ToolsMenu", () => {
  beforeEach(() => {
    cleanup();
    resetShell();
  });

  afterEach(() => resetShell());

  it("lists the tools grouped, with their shortcuts", async () => {
    publishShell({ activeConnection: "c1", engine: "postgresql", admin: true, commands: [] });

    draw();
    await open();

    expect(screen.getByText("Data")).toBeTruthy();
    expect(screen.getByText("Find a value in any table")).toBeTruthy();
    expect(screen.getByText("Ctrl+D")).toBeTruthy();
    expect(screen.getByText("Command palette")).toBeTruthy();
  });

  it("asks the dock to run the command rather than opening anything itself", async () => {
    publishShell({ activeConnection: "c1", engine: "postgresql", admin: true, commands: [] });
    const asked: string[] = [];
    const off = onShell("command", id => asked.push(id));

    draw();
    await open();
    fireEvent.click(screen.getByText("Find a value in any table"));

    expect(asked).toEqual(["tool.datasearch"]);
    off();
  });

  it("opens the palette through the bus", async () => {
    publishShell({ activeConnection: "c1", engine: "postgresql", admin: true, commands: [] });
    const opened = vi.fn();
    const off = onShell("palette", opened);

    draw();
    await open();
    fireEvent.click(screen.getByText("Command palette"));

    expect(opened).toHaveBeenCalled();
    off();
  });

  it("leaves administration out for anybody who is not an admin", async () => {
    publishShell({ activeConnection: "c1", engine: "postgresql", admin: false, commands: [] });

    draw();
    await open();

    expect(screen.queryByText("Administration")).toBeNull();
    expect(screen.queryByText("Server")).toBeNull();
  });

  it("offers the Redis browser only on Redis", async () => {
    publishShell({ activeConnection: "c1", engine: "redis", admin: true, commands: [] });

    draw();
    await open();

    expect(screen.getByText("Redis key browser")).toBeTruthy();
  });

  it("disables what needs a connection when none is selected", async () => {
    publishShell({ activeConnection: "", engine: "", admin: true, commands: [] });

    draw();
    await open();

    // Mantine marks a disabled item with data-disabled; the entry is still visible, which is what
    // tells somebody the tool exists.
    expect(screen.getByText("ER diagram").closest("[data-disabled]")).not.toBeNull();
    expect(screen.getByText("Query history").closest("[data-disabled]")).toBeNull();
  });
});

describe("the shell bus", () => {
  beforeEach(() => {
    cleanup();
    resetShell();
  });

  it("carries a payload to whoever is listening", () => {
    const seen: string[] = [];
    const off = onShell("use-sql", sql => seen.push(sql));

    emit("use-sql", "SELECT 1");
    emit("use-sql", "SELECT 2");
    off();
    emit("use-sql", "SELECT 3");

    // Unsubscribed means unsubscribed: the third one is nobody's business any more.
    expect(seen).toEqual(["SELECT 1", "SELECT 2"]);
  });

  it("publishes what the header needs to render", () => {
    publishShell({ activeConnection: "c1", engine: "duckdb", admin: false, commands: [] });

    draw();

    // Rendered from the snapshot rather than from props: the header is outside the dock.
    expect(screen.getByRole("button", { name: "Tools" })).toBeTruthy();
  });
});
