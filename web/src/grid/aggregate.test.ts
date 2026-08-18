import { describe, expect, it } from "vitest";
import { summarizeSelection } from "./aggregate";

describe("summarizeSelection", () => {
  it("counts every selected cell", () =>
    expect(summarizeSelection([1, "x", null]).count).toBe(3));

  it("aggregates only the numeric cells", () => {
    const s = summarizeSelection([1, 2, "x", null, 3]);
    expect(s.numeric).toBe(3);
    expect(s.sum).toBe(6);
    expect(s.avg).toBe(2);
    expect(s.min).toBe(1);
    expect(s.max).toBe(3);
  });

  it("treats numeric strings as numbers", () =>
    expect(summarizeSelection(["1.5", "2.5"]).sum).toBe(4));

  it("returns null aggregates when nothing is numeric", () => {
    const s = summarizeSelection(["a", null]);
    expect(s.sum).toBeNull();
    expect(s.avg).toBeNull();
  });

  it("ignores booleans so a bit column does not read as 1 and 0", () =>
    expect(summarizeSelection([true, false]).numeric).toBe(0));
});
