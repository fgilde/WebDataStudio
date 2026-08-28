import { describe, it, expect } from "vitest";
import { comparePlans, describeComparison } from "./comparePlans";
import type { PlanNodeDto } from "../api";

const node = (operation: string, actualMs: number | null, children: PlanNodeDto[] = [],
  actualRows: number | null = null, detail: string | null = null): PlanNodeDto => ({
  operation, detail, estimatedCost: null, estimatedRows: null, actualRows, actualMs,
  children, warnings: [],
});

describe("two plans of the same statement", () => {
  it("says which node got slower and which got faster", () => {
    const before = node("Nested Loop", 10, [node("Seq Scan", 8), node("Index Scan", 1)]);
    const after = node("Nested Loop", 12, [node("Seq Scan", 11), node("Index Scan", 1)]);

    const diff = comparePlans(before, after);

    expect(diff.find(one => one.operation === "Seq Scan")?.status).toBe("slower");
    expect(diff.find(one => one.operation === "Index Scan")?.status).toBe("same");
  });

  /// The point of the comparison: the scan that is gone, and the index scan that replaced it.
  it("marks what appeared and what is no longer there", () => {
    const before = node("Nested Loop", 10, [node("Seq Scan", 9)]);
    const after = node("Nested Loop", 2, [node("Index Scan", 1)]);

    const diff = comparePlans(before, after);

    expect(diff.find(one => one.operation === "Index Scan")?.status).toBe("added");
    expect(diff.find(one => one.operation === "Seq Scan")?.status).toBe("gone");
  });

  it("keeps the tree's shape so a node is read where it sits", () => {
    const plan = node("Hash Join", 5, [node("Seq Scan", 2), node("Hash", 3, [node("Seq Scan", 3)])]);

    const diff = comparePlans(plan, plan);

    expect(diff.map(one => one.depth)).toEqual([0, 1, 1, 2]);
    expect(diff.every(one => one.status === "same")).toBe(true);
  });

  /// Measurements wobble. A plan where every line says "slower by 0.05 ms" tells nobody anything.
  it("treats a wobble as no change", () => {
    const diff = comparePlans(node("Seq Scan", 4.00), node("Seq Scan", 4.05));

    expect(diff[0].status).toBe("same");
  });

  it("compares nothing against the first plan there ever was", () => {
    expect(comparePlans(null, node("Seq Scan", 1))).toHaveLength(1);
    expect(comparePlans(null, node("Seq Scan", 1))[0].status).toBe("added");
    expect(comparePlans(node("Seq Scan", 1), null)[0].status).toBe("gone");
  });

  it("says the whole thing in one line", () => {
    const before = node("Nested Loop", 10, [node("Seq Scan", 9)]);
    const after = node("Nested Loop", 2, [node("Index Scan", 1)]);

    const sentence = describeComparison(comparePlans(before, after));

    expect(sentence).toContain("faster overall");
    expect(sentence).toContain("1 new");
    expect(sentence).toContain("1 gone");
  });

  it("says so when nothing moved", () => {
    const plan = node("Seq Scan", 3);

    expect(describeComparison(comparePlans(plan, plan))).toBe("nothing moved");
  });

  /// An estimated plan has no times at all, and a comparison of two of them must not claim
  /// improvements it cannot measure.
  it("claims nothing when there are no times to compare", () => {
    const diff = comparePlans(node("Seq Scan", null), node("Seq Scan", null));

    expect(diff[0].status).toBe("same");
    expect(describeComparison(diff)).toBe("nothing moved");
  });
});
