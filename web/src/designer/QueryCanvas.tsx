import { useEffect } from "react";
import {
  Background, Controls, Handle, Position, ReactFlow, ReactFlowProvider,
  useEdgesState, useNodesState, useReactFlow,
  type Connection, type Edge, type Node, type NodeProps,
} from "@xyflow/react";
import { ActionIcon, Checkbox, Group, Text } from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import "@xyflow/react/dist/style.css";
import { applyConnection, toGraph, type TableNodeData } from "./canvasModel";
import type { LoadedTable, QueryModel } from "./buildSelect";

/// One table, with a checkbox per column: ticking it selects the column, which is the thing people
/// come to a query builder for. The handles on both sides are what a join is dragged between.
function QueryTableNode({ data }: NodeProps) {
  const node = data as TableNodeData;
  const selected = (node.selectedColumns as string[]) ?? [];
  const toggle = node.onToggleColumn as ((column: string) => void) | undefined;

  return (
    <div style={{
      width: 200, background: "var(--mantine-color-body)", borderRadius: 6,
      border: "1px solid var(--mantine-color-default-border)", overflow: "hidden",
    }}>
      <Handle type="target" position={Position.Left} />
      <Group gap={6} px={8} py={4} bg="var(--mantine-color-default)" wrap="nowrap" justify="space-between">
        <Group gap={6} wrap="nowrap">
          <Text size="xs" fw={700}>{node.label}</Text>
          <Text size="10px" c="dimmed">{node.alias}</Text>
        </Group>
        <ActionIcon size="xs" variant="subtle" color="red" aria-label={`Remove ${node.label}`}
          onClick={() => (node.onRemove as (() => void) | undefined)?.()}>
          <IconX size={12} />
        </ActionIcon>
      </Group>

      <div style={{ padding: "2px 6px", maxHeight: 260, overflowY: "auto" }}>
        {node.columns.length === 0
          ? <Text size="10px" c="dimmed" p={4}>no columns</Text>
          : node.columns.map(column => (
            <Checkbox key={column} size="xs" label={column} styles={{ label: { fontSize: 11 } }}
              checked={selected.includes(column)}
              onChange={() => toggle?.(column)} />
          ))}
      </div>
      <Handle type="source" position={Position.Right} />
    </div>
  );
}

const nodeTypes = { queryTable: QueryTableNode };

function Canvas({ model, loaded, onModelChange }: {
  model: QueryModel;
  loaded: LoadedTable[];
  onModelChange: (next: QueryModel) => void;
}) {
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<TableNodeData>>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const flow = useReactFlow();

  // The model owns the tables and joins; the canvas is rebuilt from it, with the columns a user
  // has selected and the toggle handed to each node so a click lands back in the model.
  useEffect(() => {
    const graph = toGraph(model, loaded);

    setNodes(graph.nodes.map(node => ({
      ...node,
      data: {
        ...node.data,
        selectedColumns: model.columns
          .filter(column => column.table === node.id)
          .map(column => column.column),
        onRemove: () => onModelChange({
          ...model,
          tables: model.tables.filter(table => table.alias !== node.id),
          joins: model.joins.filter(join => join.left !== node.id && join.right !== node.id),
          columns: model.columns.filter(column => column.table !== node.id),
          filters: model.filters.filter(filter => filter.table !== node.id),
          order: model.order.filter(entry => entry.table !== node.id),
        }),
        onToggleColumn: (column: string) => onModelChange({
          ...model,
          columns: model.columns.some(c => c.table === node.id && c.column === column)
            ? model.columns.filter(c => !(c.table === node.id && c.column === column))
            : [...model.columns, { table: node.id, column }],
        }),
      },
    })));

    setEdges(graph.edges);

    // fitView only runs on mount, so a table added later would sit outside the viewport — which is
    // exactly what happens every time somebody builds a query.
    window.setTimeout(() => flow.fitView({ padding: 0.2, duration: 200 }), 0);
    // Positions are recomputed whenever the shape changes; dragging in between is local to
    // react-flow and deliberately not written back.
  }, [model, loaded, onModelChange, setNodes, setEdges, flow]);

  return (
    <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes}
      onNodesChange={onNodesChange} onEdgesChange={onEdgesChange}
      onConnect={(connection: Connection) => {
        if (!connection.source || !connection.target) return;
        onModelChange(applyConnection(model, loaded,
          { source: connection.source, target: connection.target }));
      }}
      onEdgeDoubleClick={(_, edge) => onModelChange({
        ...model,
        joins: model.joins.filter((_join, index) => `j${index}` !== edge.id),
      })}
      fitView proOptions={{ hideAttribution: true }}>
      <Background />
      <Controls showInteractive={false} />
    </ReactFlow>
  );
}

export function QueryCanvas(props: {
  model: QueryModel;
  loaded: LoadedTable[];
  onModelChange: (next: QueryModel) => void;
}) {
  return (
    <ReactFlowProvider>
      <Canvas {...props} />
    </ReactFlowProvider>
  );
}
