import dagre from "@dagrejs/dagre";
import { openPlan, type AnalyzeResultDto, type PlanNodeDto, type PlanPropertyDto } from "../api";

export type OperatorFamily =
  | "seek" | "scan" | "lookup" | "join" | "aggregate" | "sort" | "spool" | "compute" | "filter"
  | "parallelism" | "write" | "other";

/// The operator names of every engine, reduced to what an icon can say. Order matters: "Index
/// Seek" before "Index", "Clustered Index Update" before "Scan".
const FAMILIES: [RegExp, OperatorFamily][] = [
  [/insert|update|delete|merge$/i, "write"],
  [/seek/i, "seek"],
  [/lookup/i, "lookup"],
  [/scan/i, "scan"],
  [/loop|hash|merge join|join/i, "join"],
  [/aggregate|group/i, "aggregate"],
  [/sort|top n/i, "sort"],
  [/spool/i, "spool"],
  [/compute|scalar|project/i, "compute"],
  [/filter/i, "filter"],
  [/parallelism|gather|repartition|distribute/i, "parallelism"],
];

export const operatorFamily = (operation: string): OperatorFamily =>
  FAMILIES.find(([pattern]) => pattern.test(operation))?.[1] ?? "other";

/// What this operator costs by itself. The engine reports the whole subtree; SSMS's percentages
/// are this, and rounding in the plan can make it dip below zero.
export const ownCost = (node: PlanNodeDto): number =>
  Math.max(0, (node.estimatedCost ?? 0) - node.children.reduce((sum, c) => sum + (c.estimatedCost ?? 0), 0));

export const costShare = (node: PlanNodeDto, statementCost: number | null): number | null =>
  statementCost && statementCost > 0 ? ownCost(node) / statementCost : null;

/// The rows an operator hands to its parent: what happened if the plan ran, else the guess.
export const rowsOut = (node: PlanNodeDto): number | null => node.actualRows ?? node.estimatedRows ?? null;

/// Logarithmic, as in SSMS: a million rows should look heavier than a thousand, not a thousand
/// times heavier.
export const edgeWidth = (rows: number | null): number =>
  rows === null ? 1 : Math.min(12, 1 + Math.log10(Math.max(1, rows)) * 1.2);

export interface FlatPlanNode { node: PlanNodeDto; id: string; parentId: string | null }

/// Ids are paths ("0.1.0"), stable for the same plan, so a selection survives a re-render.
export function flattenPlan(root: PlanNodeDto): FlatPlanNode[] {
  const out: FlatPlanNode[] = [];
  const walk = (node: PlanNodeDto, id: string, parentId: string | null) => {
    out.push({ node, id, parentId });
    node.children.forEach((child, i) => walk(child, `${id}.${i}`, id));
  };
  walk(root, "0", null);
  return out;
}

export const NODE_WIDTH = 190;
export const NODE_HEIGHT = 96;

export interface PositionedPlanNode { id: string; node: PlanNodeDto; position: { x: number; y: number } }
export interface PlanEdge { id: string; source: string; target: string; rows: number | null }

/// Right to left, the way SSMS draws it: the statement's result on the left, the tables on the
/// right, and every arrow pointing at the operator that consumes the rows.
export function layoutPlan(root: PlanNodeDto): { nodes: PositionedPlanNode[]; edges: PlanEdge[] } {
  const flat = flattenPlan(root);
  const graph = new dagre.graphlib.Graph();
  graph.setGraph({ rankdir: "LR", nodesep: 24, ranksep: 70 });
  graph.setDefaultEdgeLabel(() => ({}));

  for (const { id } of flat) graph.setNode(id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  for (const { id, parentId } of flat) if (parentId) graph.setEdge(parentId, id);
  dagre.layout(graph);

  return {
    nodes: flat.map(({ id, node }) => {
      const { x, y } = graph.node(id);
      return { id, node, position: { x: x - NODE_WIDTH / 2, y: y - NODE_HEIGHT / 2 } };
    }),
    edges: flat.filter(f => f.parentId).map(({ id, node, parentId }) => ({
      id: `${id}->${parentId}`, source: id, target: parentId!, rows: rowsOut(node),
    })),
  };
}

/// Keeps a property when its name or value matches, or when something below it does — so the path
/// to a match stays visible, as in a property grid's search.
export function filterProperties(props: PlanPropertyDto[], query: string): PlanPropertyDto[] {
  const q = query.trim().toLowerCase();
  if (!q) return props;
  return props.flatMap(p => {
    if (p.name.toLowerCase().includes(q) || (p.value ?? "").toLowerCase().includes(q)) return [p];
    const children = filterProperties(p.children, q);
    return children.length > 0 ? [{ ...p, children }] : [];
  });
}

/// The plan in its engine's own format, so SSMS or Rider opens it as if it had written it.
export function savePlanFile(raw: string, name: string, extension: "sqlplan" | "xml") {
  const url = URL.createObjectURL(new Blob([encodePlanBytes(raw)], { type: "application/xml" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `${name}.${extension}`;
  link.click();
  // Not in the same tick: some browsers cancel a download of megabytes whose URL is already gone.
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/// A plan file from disk, read on the server — the one parser, so a file gets the same findings as
/// a plan fetched live.
export const openPlanFile = async (file: File): Promise<AnalyzeResultDto> =>
  openPlan(decodePlanBytes(await file.arrayBuffer()));

/// SSMS saves plans as UTF-16 with a byte order mark; `File.text()` would read that as UTF-8 and
/// hand the server noise. The mark says which it is; without one it is UTF-8.
export function decodePlanBytes(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  const encoding = bytes[0] === 0xff && bytes[1] === 0xfe ? "utf-16le"
    : bytes[0] === 0xfe && bytes[1] === 0xff ? "utf-16be"
      : "utf-8";
  // TextDecoder drops the byte order mark of the encoding it was given.
  return new TextDecoder(encoding).decode(bytes);
}

/// The other way round, in the shape SSMS writes: UTF-16 LE behind its byte order mark. The XML
/// inside says encoding="utf-16", and a reader that trusts that would refuse UTF-8 bytes.
export function encodePlanBytes(text: string): Uint8Array<ArrayBuffer> {
  const bytes = new Uint8Array(2 + text.length * 2);
  bytes[0] = 0xff;
  bytes[1] = 0xfe;
  for (let i = 0; i < text.length; i++) {
    const unit = text.charCodeAt(i);
    bytes[2 + i * 2] = unit & 0xff;
    bytes[3 + i * 2] = unit >> 8;
  }
  return bytes;
}
