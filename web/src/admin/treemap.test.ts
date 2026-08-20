import { describe, expect, it } from "vitest";
import { squarify } from "./treemap";

describe("squarify", () => {
  const items = [
    { label: "orders", bytes: 500 },
    { label: "people", bytes: 300 },
    { label: "logs", bytes: 150 },
    { label: "settings", bytes: 50 },
  ];

  it("puts the largest item first", () => {
    expect(squarify(items, 200, 100)[0].label).toBe("orders");
  });

  it("gives every item a rectangle", () => {
    expect(squarify(items, 200, 100)).toHaveLength(4);
  });

  it("fills the box", () => {
    const area = squarify(items, 200, 100)
      .reduce((sum, rect) => sum + rect.width * rect.height, 0);

    // Within a rounding error of the box: the areas are proportional to the sizes.
    expect(area).toBeGreaterThan(200 * 100 * 0.95);
    expect(area).toBeLessThanOrEqual(200 * 100 * 1.01);
  });

  it("keeps every rectangle inside the box and positive", () => {
    for (const rect of squarify(items, 200, 100)) {
      expect(rect.width).toBeGreaterThan(0);
      expect(rect.height).toBeGreaterThan(0);
      expect(rect.x).toBeGreaterThanOrEqual(-0.001);
      expect(rect.y).toBeGreaterThanOrEqual(-0.001);
      expect(rect.x + rect.width).toBeLessThanOrEqual(200.001);
      expect(rect.y + rect.height).toBeLessThanOrEqual(100.001);
    }
  });

  it("scales areas with the sizes", () => {
    const rects = squarify([{ label: "big", bytes: 900 }, { label: "small", bytes: 100 }], 100, 100);
    const big = rects.find(rect => rect.label === "big")!;
    const small = rects.find(rect => rect.label === "small")!;

    const ratio = (big.width * big.height) / (small.width * small.height);
    expect(ratio).toBeGreaterThan(6);
  });

  it("has nothing to draw for an empty list, a zero size or a zero box", () => {
    expect(squarify([], 100, 100)).toEqual([]);
    expect(squarify([{ label: "empty", bytes: 0 }], 100, 100)).toEqual([]);
    expect(squarify(items, 0, 100)).toEqual([]);
  });
});
