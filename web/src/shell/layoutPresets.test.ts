import { describe, expect, it } from "vitest";
import { presetForSlot, visiblePresets, type LayoutPreset } from "./LayoutPresets";

const preset = (name: string, connectionId: string | null): LayoutPreset =>
  ({ name, connectionId, layout: { name } });

describe("layout preset slots", () => {
  const presets = [preset("global", null), preset("shop", "c1"), preset("other", "c2")];

  it("numbers the presets the current connection can see", () => {
    expect(visiblePresets(presets, "c1").map(p => p.name)).toEqual(["global", "shop"]);
  });

  it("resolves a slot 1-based, matching the number shown in the list", () => {
    expect(presetForSlot(presets, "c1", 1)?.name).toBe("global");
    expect(presetForSlot(presets, "c1", 2)?.name).toBe("shop");
  });

  it("has nothing for an empty slot", () => {
    expect(presetForSlot(presets, "c1", 3)).toBeUndefined();
    expect(presetForSlot(presets, "c1", 0)).toBeUndefined();
  });
});
