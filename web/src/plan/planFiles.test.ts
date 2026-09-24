import { describe, expect, it } from "vitest";
import { planFiles } from "./planFiles";

describe("planFiles", () => {
  it("hands out distinct ids and gives the plan back", () => {
    const result = { plan: null, summary: null, planError: null, findings: [] };
    const a = planFiles.put(result);
    const b = planFiles.put(result);
    expect(a).not.toBe(b);
    expect(planFiles.get(a)).toBe(result);
    planFiles.drop(a);
    expect(planFiles.get(a)).toBeUndefined();
  });
});
