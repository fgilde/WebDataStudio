import { describe, expect, it } from "vitest";
import { heatColor, heatRatio } from "./heat";

describe("heatColor", () => {
  it("returns the coolest colour for zero cost", () =>
    expect(heatColor(0, 100)).toContain("blue"));

  it("returns the hottest colour at max cost", () =>
    expect(heatColor(100, 100)).toContain("red"));

  it("returns neither extreme in the middle", () => {
    const mid = heatColor(50, 100);
    expect(mid).not.toContain("blue");
    expect(mid).not.toContain("red");
  });

  it("does not divide by zero when the plan carries no costs", () =>
    expect(heatColor(0, 0)).toContain("blue"));

  it("clamps a cost above the maximum", () => expect(heatRatio(500, 100)).toBe(1));
  it("handles a non-finite cost", () => expect(heatRatio(Number.NaN, 100)).toBe(0));
});
