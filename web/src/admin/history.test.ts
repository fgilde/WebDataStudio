import { describe, expect, it } from "vitest";
import { sparklinePath } from "./history";

describe("sparklinePath", () => {
  it("draws a line through the samples, scaled into the box", () => {
    const path = sparklinePath([0, 5, 10], 100, 20);

    // First sample at the bottom, last at the top, because the value grew.
    expect(path).toBe("M0.0,20.0 L50.0,10.0 L100.0,0.0");
  });

  it("draws a flat line for flat data rather than dividing by zero", () => {
    const path = sparklinePath([7, 7, 7], 60, 10);

    expect(path).toBe("M0.0,10.0 L30.0,10.0 L60.0,10.0");
  });

  it("draws a single sample as a horizontal line", () => {
    expect(sparklinePath([3], 40, 10)).toBe("M0,5 L40,5");
  });

  it("has nothing to draw without samples", () => {
    expect(sparklinePath([], 40, 10)).toBe("");
  });
});
