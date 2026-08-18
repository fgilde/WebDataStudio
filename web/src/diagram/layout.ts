import dagre from "@dagrejs/dagre";
import type { DiagramEdgeDto, DiagramNodeDto } from "../api";

export interface PlacedNode { id: string; x: number; y: number; width: number; height: number }

const HEADER = 28;
const ROW = 18;
const WIDTH = 220;

export const heightOf = (node: DiagramNodeDto) => HEADER + Math.min(node.columns.length, 18) * ROW + 6;

/// Dagre gives a left-to-right layered layout, which reads like an ER diagram: referenced tables
/// end up left of the tables that point at them.
export function layout(nodes: DiagramNodeDto[], edges: DiagramEdgeDto[]): PlacedNode[] {
  const graph = new dagre.graphlib.Graph();
  graph.setGraph({ rankdir: "LR", nodesep: 40, ranksep: 90, marginx: 20, marginy: 20 });
  graph.setDefaultEdgeLabel(() => ({}));

  for (const node of nodes) graph.setNode(node.id, { width: WIDTH, height: heightOf(node) });

  // Only edges between tables we actually drew: dagre would otherwise invent a node for the
  // missing side and place a phantom box.
  const known = new Set(nodes.map(n => n.id));
  for (const edge of edges)
    if (known.has(edge.source) && known.has(edge.target)) graph.setEdge(edge.source, edge.target);

  dagre.layout(graph);

  return nodes.map(node => {
    const placed = graph.node(node.id);
    return {
      id: node.id,
      // dagre centres its nodes; react-flow positions by the top-left corner.
      x: placed.x - placed.width / 2,
      y: placed.y - placed.height / 2,
      width: placed.width,
      height: placed.height,
    };
  });
}
