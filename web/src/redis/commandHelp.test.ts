import { describe, expect, it } from "vitest";
import { describe as describeHelp, indexCommands, suggest } from "./commandHelp";

const commands = [
  { name: "hset", arity: -4, summary: "Sets the value of a field in a hash.", group: "hash", since: "2.0.0" },
  { name: "hget", arity: 3, summary: "Returns the value of a field in a hash.", group: "hash", since: "2.0.0" },
  { name: "get", arity: 2, summary: "Returns the value of a key.", group: "string", since: "1.0.0" },
  { name: "json.set", arity: -4, summary: "Sets JSON at a path.", group: "json", since: "1.0.0" },
];

describe("command help", () => {
  it("keeps arity and summary per command, upper-cased", () => {
    const index = indexCommands(commands);
    const hset = index.get("HSET");

    expect(hset?.arity).toBe(-4);
    expect(hset?.summary).toContain("field in a hash");
    expect(hset?.group).toBe("hash");
  });

  it("suggests the hash commands for hs", () => {
    const suggestions = suggest(indexCommands(commands), "hs");

    expect(suggestions.map(s => s.name)).toEqual(["HSET"]);
  });

  it("puts prefix matches before the rest", () => {
    const suggestions = suggest(indexCommands(commands), "get");

    // GET starts with it; HGET only contains it.
    expect(suggestions.map(s => s.name)).toEqual(["GET", "HGET"]);
  });

  it("is case-insensitive and ignores surrounding space", () => {
    expect(suggest(indexCommands(commands), "  HgE ").map(s => s.name)).toEqual(["HGET"]);
  });

  /// A module's commands come from the server, so they have to survive the index unharmed.
  it("keeps a module command", () => {
    expect(suggest(indexCommands(commands), "json").map(s => s.name)).toEqual(["JSON.SET"]);
  });

  it("offers everything for an empty prefix", () => {
    expect(suggest(indexCommands(commands), "")).toHaveLength(4);
  });

  it("describes a command in one line", () => {
    const help = indexCommands(commands).get("HGET")!;

    expect(describeHelp(help)).toBe(
      "Returns the value of a field in a hash. · group: hash · arity: 3 · since: 2.0.0");
  });

  // COMMAND INFO gives no summary; the entry is still worth having for completion.
  it("handles a command with no summary", () => {
    const help = indexCommands([{ name: "ping", arity: -1, summary: "", group: "", since: "" }]).get("PING")!;

    expect(describeHelp(help)).toBe("arity: -1");
  });
});
