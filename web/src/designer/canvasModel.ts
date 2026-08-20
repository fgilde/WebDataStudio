import dagre from "@dagrejs/dagre";
import type { Edge, Node } from "@xyflow/react";
import { suggestJoin, type LoadedTable, type QueryModel } from "./buildSelect";

export interface TableNodeData {
  label: string;
  alias: string;
  columns: string[];
  [key: string]: unknown;
}

const HEADER = 30;
const ROW = 18;
const WIDTH = 200;
const MAX_ROWS = 14;

const heightOf = (columns: number) => HEADER + Math.min(columns, MAX_ROWS) * ROW + 8;

/// The model is the truth; the canvas is a view of it. Positions come from dagre on the way in and
/// from the user's dragging afterwards, which is why the canvas keeps them and the model does not.
export function toGraph(model: QueryModel, loaded: LoadedTable[]): {
  nodes: Node<TableNodeData>[];
  edges: Edge[];
} {
  const columnsOf = (alias: string) => loaded.find(t => t.alias === alias)?.columns ?? [];

  const graph = new dagre.graphlib.Graph();
  graph.setGraph({ rankdir: "LR", nodesep: 40, ranksep: 90, marginx: 20, marginy: 20 });
  graph.setDefaultEdgeLabel(() => ({}));

  for (const table of model.tables)
    graph.setNode(table.alias, { width: WIDTH, height: heightOf(columnsOf(table.alias).length) });

  const known = new Set(model.tables.map(t => t.alias));
  for (const join of model.joins)
    if (known.has(join.left) && known.has(join.right)) graph.setEdge(join.left, join.right);

  dagre.layout(graph);

  const nodes = model.tables.map(table => {
    const placed = graph.node(table.alias);

    return {
      id: table.alias,
      type: "queryTable",
      // dagre centres a node; react-flow positions by the top-left corner.
      position: { x: placed.x - placed.width / 2, y: placed.y - placed.height / 2 },
      data: { label: table.name, alias: table.alias, columns: columnsOf(table.alias) },
    } satisfies Node<TableNodeData>;
  });

  const edges = model.joins.map((join, index) => ({
    id: `j${index}`,
    source: join.left,
    target: join.right,
    label: join.kind,
    animated: false,
  } satisfies Edge));

  return { nodes, edges };
}

/// A line dragged between two tables becomes a join. The foreign key decides the columns where
/// there is one; otherwise the first column of each side is a starting point the user corrects,
/// which is still less typing than an empty row in a form.
export function applyConnection(
  model: QueryModel,
  loaded: LoadedTable[],
  connection: { source: string; target: string },
): QueryModel {
  const { source, target } = connection;
  if (source === target) return model;

  const already = model.joins.some(join =>
    (join.left === source && join.right === target) || (join.left === target && join.right === source));
  if (already) return model;

  const left = loaded.find(t => t.alias === source);
  const right = loaded.find(t => t.alias === target);
  if (!left || !right) return model;

  const suggested = suggestJoin(left, right);

  return {
    ...model,
    joins: [...model.joins, suggested
      ? {
        left: suggested.left, leftColumn: suggested.leftColumn,
        right: suggested.right, rightColumn: suggested.rightColumn, kind: suggested.kind,
      }
      : {
        left: source, leftColumn: left.columns[0] ?? "",
        right: target, rightColumn: right.columns[0] ?? "", kind: "INNER" as const,
      }],
  };
}
