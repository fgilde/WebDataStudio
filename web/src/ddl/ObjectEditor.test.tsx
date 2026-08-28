// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { cleanup, render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { source } from "./viewSource";
import { ScriptConfirm } from "./ScriptConfirm";

const applyDdl = vi.fn();
vi.mock("../api", () => ({ applyDdl: (...args: unknown[]) => applyDdl(...args) }));

describe("the source a view opens with", () => {
  it("is the SELECT, not the CREATE around it", () => {
    expect(source("CREATE VIEW active AS SELECT id FROM people", "view"))
      .toBe("SELECT id FROM people");

    expect(source("CREATE OR REPLACE VIEW public.active AS\n  SELECT 1", "view"))
      .toBe("SELECT 1");

    expect(source("CREATE OR ALTER VIEW dbo.active AS SELECT 1", "view")).toBe("SELECT 1");
    expect(source("CREATE MATERIALIZED VIEW m AS SELECT 1", "view")).toBe("SELECT 1");
  });

  /// A wrong guess here would silently drop half of somebody's SQL, so anything that does not look
  /// like a view definition is kept whole.
  it("keeps a definition it does not recognise", () => {
    expect(source("-- nothing like a view", "view")).toBe("-- nothing like a view");
    expect(source("", "view")).toBe("");
  });

  it("leaves a routine exactly as the engine wrote it", () => {
    const body = "CREATE FUNCTION ship() RETURNS int AS $$ SELECT 1 $$ LANGUAGE sql";

    expect(source(body, "function")).toBe(body);
    expect(source("CREATE TRIGGER t AFTER INSERT ON people BEGIN SELECT 1; END", "trigger"))
      .toContain("CREATE TRIGGER");
  });
});

const wrap = (ui: React.ReactNode) => render(<MantineProvider>{ui}</MantineProvider>);

describe("the statement shown before it runs", () => {
  beforeEach(() => { cleanup(); applyDdl.mockReset(); applyDdl.mockResolvedValue(undefined); });

  it("shows the script and runs nothing until it is asked to", async () => {
    wrap(<ScriptConfirm
      pending={{ connectionId: "c1", title: "Drop view active", hash: "h1",
        script: "DROP VIEW active;", destructive: true }}
      onClose={() => {}} />);

    expect(screen.getByText("DROP VIEW active;")).toBeTruthy();
    expect(applyDdl).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Run it" }));
    await waitFor(() => expect(applyDdl).toHaveBeenCalledWith("c1", "h1"));
  });

  /// A rename or a drop breaks a view somebody else wrote. Worth seeing before, not after.
  it("names what reads this object", () => {
    wrap(<ScriptConfirm
      pending={{ connectionId: "c1", title: "Drop table people", hash: "h1",
        script: "DROP TABLE people;",
        dependencies: { dependsOn: [], usedBy: ["active_people"], bestEffort: false } }}
      onClose={() => {}} />);

    expect(screen.getByText(/active_people/)).toBeTruthy();
  });

  it("is not open when there is nothing pending", () => {
    wrap(<ScriptConfirm pending={null} onClose={() => {}} />);

    expect(screen.queryByRole("button", { name: "Run it" })).toBeNull();
  });
});
