import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Background, Controls, Handle, MiniMap, Position, ReactFlow, ReactFlowProvider,
  type Edge, type Node, type NodeProps, useEdgesState, useNodesState, useReactFlow,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { ActionIcon, Alert, Group, Loader, Select, Text, Tooltip } from "@mantine/core";
import { IconDownload, IconKey, IconLink, IconRefresh } from "@tabler/icons-react";
import { loadDiagram, type DiagramEdgeDto, type DiagramNodeDto } from "../api";
import { heightOf, layout } from "./layout";

type TableNodeData = { table: DiagramNodeDto };

function TableNode({ data }: NodeProps<Node<TableNodeData>>) {
  const { table } = data;

  return (
    <div style={{
      border: "1px solid var(--mantine-color-default-border)",
      borderRadius: 6,
      background: "var(--mantine-color-body)",
      fontSize: 11,
      overflow: "hidden",
      width: 220,
    }}>
      <Handle type="target" position={Position.Left} style={{ background: "var(--mantine-primary-color-filled)" }} />
      <div style={{
        padding: "5px 8px", fontWeight: 600, fontSize: 12,
        background: "var(--mantine-color-default-hover)",
        borderBottom: "1px solid var(--mantine-color-default-border)",
      }}>
        {table.name}
        {table.schema ? <Text span size="10px" c="dimmed"> · {table.schema}</Text> : null}
      </div>

      {/* Long tables are cut off rather than turned into a wall; the structure panel has it all. */}
      {table.columns.slice(0, 18).map(column => (
        <div key={column.name} style={{
          display: "flex", gap: 6, alignItems: "center", padding: "1px 8px", height: 18,
        }}>
          {column.primaryKey ? <IconKey size={10} /> : column.foreignKey ? <IconLink size={10} /> : <span style={{ width: 10 }} />}
          <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
            {column.name}
          </span>
          <Text span size="10px" c="dimmed">{column.type}</Text>
        </div>
      ))}
      {table.columns.length > 18
        ? <Text size="10px" c="dimmed" px={8}>+{table.columns.length - 18} more</Text>
        : null}

      <Handle type="source" position={Position.Right} style={{ background: "var(--mantine-primary-color-filled)" }} />
    </div>
  );
}

const nodeTypes = { table: TableNode };

function Canvas({ connectionId }: { connectionId: string }) {
  const flow = useReactFlow();
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<TableNodeData>>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const [schemas, setSchemas] = useState<string[]>([]);
  const [schema, setSchema] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const build = useCallback((data: { nodes: DiagramNodeDto[]; edges: DiagramEdgeDto[] }) => {
    const placed = layout(data.nodes, data.edges);
    const byId = new Map(placed.map(p => [p.id, p]));

    setNodes(data.nodes.map(table => ({
      id: table.id,
      type: "table",
      position: { x: byId.get(table.id)?.x ?? 0, y: byId.get(table.id)?.y ?? 0 },
      data: { table },
      style: { width: 220, height: heightOf(table) },
    })));

    setEdges(data.edges.filter(e => e.resolved).map((edge, index) => ({
      id: `${edge.source}-${edge.target}-${edge.name}-${index}`,
      source: edge.source,
      target: edge.target,
      label: edge.sourceColumns.join(", "),
      labelStyle: { fontSize: 10 },
      animated: false,
      style: { stroke: "var(--mantine-color-dimmed)" },
    })));

    // fitView on the component only sees the first, empty graph; refit once the boxes exist.
    requestAnimationFrame(() => flow.fitView({ padding: 0.15 }));
  }, [setNodes, setEdges, flow]);

  const refresh = useCallback(() => {
    setLoading(true);
    setError(null);

    loadDiagram(connectionId, schema ?? undefined)
      .then(data => {
        build(data);
        setSchemas([...new Set(data.nodes.map(n => n.schema).filter(Boolean))].sort());
      })
      .catch(e => setError(e.message))
      .finally(() => setLoading(false));
  }, [connectionId, schema, build]);

  useEffect(() => { refresh(); }, [refresh]);

  // The diagram is worth keeping outside the app, so it exports as an SVG of boxes and lines.
  const exportSvg = useCallback(() => {
    const width = Math.max(...nodes.map(n => n.position.x + 240), 400);
    const height = Math.max(...nodes.map(n => n.position.y + (n.data.table.columns.length * 18 + 40)), 300);

    const boxes = nodes.map(n => {
      const rows = n.data.table.columns.slice(0, 18).map((c, i) =>
        `<text x="${n.position.x + 8}" y="${n.position.y + 42 + i * 18}" font-size="11">${escape(c.name)}</text>`);
      return [
        `<rect x="${n.position.x}" y="${n.position.y}" width="220" height="${heightOf(n.data.table)}"`,
        ` fill="none" stroke="#888" rx="6"/>`,
        `<text x="${n.position.x + 8}" y="${n.position.y + 18}" font-size="12" font-weight="600">`,
        `${escape(n.data.table.name)}</text>`,
        rows.join(""),
      ].join("");
    });

    const lines = edges.map(e => {
      const from = nodes.find(n => n.id === e.source);
      const to = nodes.find(n => n.id === e.target);
      if (!from || !to) return "";
      return `<line x1="${from.position.x + 220}" y1="${from.position.y + 14}" ` +
        `x2="${to.position.x}" y2="${to.position.y + 14}" stroke="#888"/>`;
    });

    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">` +
      `${lines.join("")}${boxes.join("")}</svg>`;

    const url = URL.createObjectURL(new Blob([svg], { type: "image/svg+xml" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "diagram.svg";
    link.click();
    URL.revokeObjectURL(url);
  }, [nodes, edges]);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <Group gap={6} p={4}>
        <Select size="xs" w={180} placeholder="All schemas" clearable data={schemas}
          value={schema} onChange={setSchema} aria-label="Schema" />
        <Tooltip label="Reload">
          <ActionIcon size="sm" variant="subtle" aria-label="Reload diagram" onClick={refresh}>
            <IconRefresh size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Export as SVG">
          <ActionIcon size="sm" variant="subtle" aria-label="Export diagram" onClick={exportSvg}>
            <IconDownload size={15} />
          </ActionIcon>
        </Tooltip>
        {loading ? <Loader size="xs" /> : <Text size="xs" c="dimmed">{nodes.length} tables</Text>}
      </Group>

      {error ? <Alert color="red" variant="light" m={4}>{error}</Alert> : null}

      <div style={{ flex: 1, minHeight: 0 }}>
        <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes}
          onNodesChange={onNodesChange} onEdgesChange={onEdgesChange} fitView minZoom={0.1}>
          <Background />
          <Controls showInteractive={false} />
          <MiniMap pannable zoomable maskColor="rgba(0,0,0,.25)"
          style={{ background: "var(--mantine-color-body)" }}
          nodeColor="var(--mantine-color-default-hover)" />
        </ReactFlow>
      </div>
    </div>
  );
}

const escape = (value: string) =>
  value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

export function DiagramPanel({ connectionId }: { connectionId: string }) {
  // ReactFlowProvider has to sit above the hooks, and remounting on a connection change is the
  // simplest way to drop the old graph's state entirely.
  return useMemo(() => (
    <ReactFlowProvider key={connectionId}>
      <Canvas connectionId={connectionId} />
    </ReactFlowProvider>
  ), [connectionId]);
}
