import { describe, expect, it, vi } from "vitest";
import { buildCommands, filterCommands, type CommandContext } from "./commands";
import { TOOLS, visibleTools } from "./tools";

const context = (patch: Partial<CommandContext> = {}): CommandContext => ({
  newQuery: vi.fn(), runCurrent: vi.fn(), cancelCurrent: vi.fn(), formatCurrent: vi.fn(),
  openConnections: vi.fn(), addConnection: vi.fn(), addBucket: vi.fn(), importFile: vi.fn(),
  refreshExplorer: vi.fn(),
  goToObject: vi.fn(), openTool: vi.fn(), saveCurrentQuery: vi.fn(), exportResult: vi.fn(),
  openSnippets: vi.fn(), showExplorer: vi.fn(), openInBuilder: vi.fn(), switchTheme: vi.fn(),
  saveLayout: vi.fn(), resetLayout: vi.fn(), copyLink: vi.fn(), showShortcuts: vi.fn(),
  openPreferences: vi.fn(),
  activeConnection: "c1", admin: true,
  ...patch,
});

describe("buildCommands", () => {
  it("offers every tool the studio has", () => {
    const ids = buildCommands(context({ engine: "redis" })).map(command => command.id);

    // The palette used to be written by hand and had never heard of Find data, the query builder or
    // the Redis browser. Now the list is the tools, so it cannot fall behind again.
    for (const tool of visibleTools({ admin: true, engine: "redis" }))
      expect(ids).toContain(tool.id);
  });

  it("has no id twice", () => {
    const ids = buildCommands(context({ engine: "redis" })).map(command => command.id);

    expect(new Set(ids).size).toBe(ids.length);
  });

  it("opens a tool through the registry rather than through its own callback", () => {
    const openTool = vi.fn();
    const commands = buildCommands(context({ openTool }));

    commands.find(command => command.id === "tool.datasearch")!.run();

    expect(openTool).toHaveBeenCalledWith(
      expect.objectContaining({ component: "datasearch", dock: "tool" }));
  });

  it("disables what needs a connection when there is none", () => {
    const commands = buildCommands(context({ activeConnection: "" }));

    expect(commands.find(command => command.id === "tool.diagram")?.disabled).toBe(true);
    // A panel is always there, so it is never disabled.
    expect(commands.find(command => command.id === "tool.history")?.disabled).toBe(false);
  });

  it("leaves out administration for anybody who is not an admin", () => {
    const ids = buildCommands(context({ admin: false })).map(command => command.id);

    expect(ids).not.toContain("tool.admin");
    expect(ids).not.toContain("tool.jobs");
    expect(ids).toContain("tool.diagram");
  });

  it("offers the Redis browser only on Redis", () => {
    expect(buildCommands(context({ engine: "postgresql" })).map(c => c.id))
      .not.toContain("tool.redis");
    expect(buildCommands(context({ engine: "redis" })).map(c => c.id)).toContain("tool.redis");
  });

  it("keeps the entry points the explorer no longer carries", () => {
    const ids = buildCommands(context()).map(command => command.id);

    // The explorer's icon row lost these; if the palette lost them too they would be unreachable.
    for (const id of ["view.saveLayout", "tool.compare", "tool.notebook", "tool.archive",
      "connection.bucket", "import.file"])
      expect(ids).toContain(id);
  });

  it("gives every command a group and a label", () => {
    for (const command of buildCommands(context({ engine: "redis" }))) {
      expect(command.label.length).toBeGreaterThan(3);
      expect(["Query", "Connections", "Tools", "View"]).toContain(command.group);
    }
  });
});

describe("filterCommands", () => {
  it("matches the label, the group and the id", () => {
    const commands = buildCommands(context());

    expect(filterCommands(commands, "find a value").map(c => c.id)).toEqual(["tool.datasearch"]);
    expect(filterCommands(commands, "tools").length).toBeGreaterThan(5);
    expect(filterCommands(commands, "view.").every(c => c.id.startsWith("view."))).toBe(true);
  });

  it("returns everything for an empty search", () => {
    const commands = buildCommands(context());

    expect(filterCommands(commands, "  ")).toHaveLength(commands.length);
  });
});

describe("TOOLS", () => {
  it("names a dock component for every entry", () => {
    for (const tool of TOOLS) {
      expect(tool.component.length).toBeGreaterThan(2);
      expect(["tool", "panel"]).toContain(tool.dock);
    }
  });

  it("uses one id per entry", () => {
    const ids = TOOLS.map(tool => tool.id);

    expect(new Set(ids).size).toBe(ids.length);
  });

  it("hides an engine-specific tool everywhere else", () => {
    expect(visibleTools({ admin: true }).map(tool => tool.id)).not.toContain("tool.redis");
    expect(visibleTools({ admin: true, engine: "redis" }).map(tool => tool.id))
      .toContain("tool.redis");
  });
});
