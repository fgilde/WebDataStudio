import type { PlanNodeDto } from "../api";

export interface PlanDiffNode {
  depth: number;
  operation: string;
  detail: string | null;
  beforeMs: number | null;
  afterMs: number | null;
  beforeRows: number | null;
  afterRows: number | null;
  /// What happened to this node between the two runs.
  status: "same" | "faster" | "slower" | "added" | "gone";
}

/// How much a node has to move before it counts as a change: measurements wobble, and a plan where
/// every line says "slower by 0.1 ms" tells nobody anything.
const NOISE = 0.1;

/// Two plans of the same statement, side by side.
///
/// The question is never "what does this plan look like" — it is "what changed since it was fast".
/// A new index, a table that grew, a parameter that hits a different branch: all of them show up as
/// one node that moved, and finding that node by reading two plans in two tabs is the part nobody
/// does. Matching is by position and operation, which is what a plan of the same statement keeps.
export function comparePlans(before: PlanNodeDto | null, after: PlanNodeDto | null): PlanDiffNode[] {
  const left = flatten(before);
  const right = flatten(after);

  const seen = new Set<number>();
  const diff: PlanDiffNode[] = [];

  right.forEach(node => {
    // The same operation at the same place in the tree is the same node. Anything else is new.
    const match = left.findIndex((one, index) =>
      !seen.has(index) && one.key === node.key && one.depth === node.depth);

    if (match >= 0) seen.add(match);

    const previous = match >= 0 ? left[match] : null;

    diff.push({
      depth: node.depth,
      operation: node.node.operation,
      detail: node.node.detail,
      beforeMs: previous?.node.actualMs ?? null,
      afterMs: node.node.actualMs,
      beforeRows: previous?.node.actualRows ?? null,
      afterRows: node.node.actualRows,
      status: previous === null ? "added" : moved(previous.node.actualMs, node.node.actualMs),
    });
  });

  // Nodes the new plan no longer has: usually the point of the comparison — the scan that is gone.
  left.forEach((node, index) => {
    if (seen.has(index)) return;

    diff.push({
      depth: node.depth,
      operation: node.node.operation,
      detail: node.node.detail,
      beforeMs: node.node.actualMs,
      afterMs: null,
      beforeRows: node.node.actualRows,
      afterRows: null,
      status: "gone",
    });
  });

  return diff;
}

function moved(before: number | null, after: number | null): PlanDiffNode["status"] {
  if (before === null || after === null) return "same";

  const change = after - before;
  if (Math.abs(change) <= NOISE) return "same";

  return change < 0 ? "faster" : "slower";
}

interface Flat { node: PlanNodeDto; depth: number; key: string }

function flatten(node: PlanNodeDto | null, depth = 0, into: Flat[] = []): Flat[] {
  if (!node) return into;

  into.push({ node, depth, key: `${depth}:${node.operation}:${node.detail ?? ""}` });
  for (const child of node.children ?? []) flatten(child, depth + 1, into);

  return into;
}

/// One sentence about the whole comparison, for somebody who is not going to read every row.
export function describeComparison(diff: PlanDiffNode[]): string {
  const faster = diff.filter(node => node.status === "faster").length;
  const slower = diff.filter(node => node.status === "slower").length;
  const added = diff.filter(node => node.status === "added").length;
  const gone = diff.filter(node => node.status === "gone").length;

  const before = total(diff, "beforeMs");
  const after = total(diff, "afterMs");

  const parts: string[] = [];

  if (before !== null && after !== null && Math.abs(after - before) > NOISE)
    parts.push(after < before
      ? `${(before - after).toFixed(1)} ms faster overall`
      : `${(after - before).toFixed(1)} ms slower overall`);

  if (slower > 0) parts.push(`${slower} slower`);
  if (faster > 0) parts.push(`${faster} faster`);
  if (added > 0) parts.push(`${added} new`);
  if (gone > 0) parts.push(`${gone} gone`);

  return parts.length === 0 ? "nothing moved" : parts.join(" · ");
}

function total(diff: PlanDiffNode[], field: "beforeMs" | "afterMs"): number | null {
  // Only the roots: a plan's node times already include their children.
  const roots = diff.filter(node => node.depth === 0);
  const values = roots.map(node => node[field]).filter((one): one is number => one !== null);

  return values.length === 0 ? null : values.reduce((sum, one) => sum + one, 0);
}
