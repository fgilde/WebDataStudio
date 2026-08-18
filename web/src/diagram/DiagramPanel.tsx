import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Background, Controls, MiniMap, ReactFlow, ReactFlowProvider,
  type Edge, type Node, useEdgesState, useNodesInitialized, useNodesState, useReactFlow,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import {
  ActionIcon, Alert, Checkbox, Group, Loader, Menu, Popover, ScrollArea, Select, Stack, Text,
  Tooltip,
} from "@mantine/core";
import { IconDownload, IconFilter, IconRefresh } from "@tabler/icons-react";
import { loadDiagram, type DiagramEdgeDto, type DiagramNodeDto } from "../api";
import { heightOf, layout } from "./layout";
import { TableNode, type TableNodeData } from "./TableNode";
import { buildSvg, downloadPng, downloadSvg, placementOf } from "./exportImage";
import { useAppTheme } from "../ThemeProvider";

const nodeTypes = { table: TableNode };

interface CanvasProps {
  connectionId: string;
  onOpenTable?: (connectionId: string, objectRef: string, name: string) => void;
}

function Canvas({ connectionId, onOpenTable }: CanvasProps) {
  const flow = useReactFlow();
  const initialised = useNodesInitialized();
  const { current } = useAppTheme();
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<TableNodeData>>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const [schemas, setSchemas] = useState<string[]>([]);
  const [schema, setSchema] = useState<string | null>(null);
  const [hidden, setHidden] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const graph = useRef<{ nodes: DiagramNodeDto[]; edges: DiagramEdgeDto[] }>({ nodes: [], edges: [] });
  const canvas = useRef<HTMLDivElement>(null);

  const openTable = useCallback((table: DiagramNodeDto) => {
    const path = table.schema ? `${table.schema}/${table.name}` : table.name;
    onOpenTable?.(connectionId, `Table:${path}`, table.name);
  }, [connectionId, onOpenTable]);

  // Drawing a subset is the difference between a readable diagram and a wall of boxes, so the
  // layout runs over the visible tables only rather than hiding finished nodes.
  const draw = useCallback((skip: Set<string>) => {
    const visible = graph.current.nodes.filter(n => !skip.has(n.id));
    const placed = layout(visible, graph.current.edges);
    const byId = new Map(placed.map(p => [p.id, p]));

    setNodes(visible.map(table => ({
      id: table.id,
      type: "table",
      position: { x: byId.get(table.id)?.x ?? 0, y: byId.get(table.id)?.y ?? 0 },
      data: { table, onOpen: openTable },
      style: { width: 220, height: heightOf(table) },
    })));

    const drawn = new Set(visible.map(n => n.id));
    setEdges(graph.current.edges
      .filter(e => e.resolved && drawn.has(e.source) && drawn.has(e.target))
      .map((edge, index) => ({
        id: `${edge.source}-${edge.target}-${edge.name}-${index}`,
        source: edge.source,
        target: edge.target,
        label: edge.sourceColumns.join(", "),
        labelStyle: { fontSize: 10 },
        style: { stroke: "var(--mantine-color-dimmed)" },
      })));

  }, [setNodes, setEdges, openTable]);

  // fitView on the component only ever sees the first, empty graph. Refitting once react-flow
  // reports the nodes measured is the only moment their real sizes are known.
  // A dock panel has no size until it is shown, and fitView on a zero-height container does
  // nothing. Watching the canvas catches both the first paint and every later resize.
  useEffect(() => {
    const element = canvas.current;
    if (!element || !initialised || nodes.length === 0) return;

    const fit = () => {
      if (element.clientHeight > 80) flow.fitView({ padding: 0.15 });
    };

    fit();
    const observer = new ResizeObserver(fit);
    observer.observe(element);
    return () => observer.disconnect();
  }, [initialised, nodes.length, flow]);

  const refresh = useCallback((force = false) => {
    setLoading(true);
    setError(null);

    loadDiagram(connectionId, schema ?? undefined, force)
      .then(data => {
        graph.current = data;
        setSchemas([...new Set(data.nodes.map(n => n.schema).filter(Boolean))].sort());
        setHidden(new Set());
        draw(new Set());
      })
      .catch(e => setError(e.message))
      .finally(() => setLoading(false));
  }, [connectionId, schema, draw]);

  useEffect(() => { refresh(); }, [refresh]);

  const toggle = (id: string) => {
    const next = new Set(hidden);
    if (next.has(id)) next.delete(id); else next.add(id);
    setHidden(next);
    draw(next);
  };

  const svg = () => buildSvg(
    nodes.map(n => ({ node: n.data.table, place: placementOf(n.data.table, n.position.x, n.position.y) })),
    graph.current.edges,
    current.scheme === "dark");

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <Group gap={6} p={4}>
        <Select size="xs" w={170} placeholder="All schemas" clearable data={schemas}
          value={schema} onChange={setSchema} aria-label="Schema" />

        <Popover position="bottom-start" withArrow shadow="md">
          <Popover.Target>
            <Tooltip label="Choose tables">
              <ActionIcon size="sm" variant="subtle" aria-label="Choose tables">
                <IconFilter size={15} />
              </ActionIcon>
            </Tooltip>
          </Popover.Target>
          <Popover.Dropdown p="xs">
            <ScrollArea h={280} w={240}>
              <Stack gap={2}>
                {graph.current.nodes.map(table => (
                  <Checkbox key={table.id} size="xs" label={table.name}
                    checked={!hidden.has(table.id)} onChange={() => toggle(table.id)} />
                ))}
              </Stack>
            </ScrollArea>
          </Popover.Dropdown>
        </Popover>

        <Tooltip label="Reload">
          <ActionIcon size="sm" variant="subtle" aria-label="Reload diagram" onClick={() => refresh(true)}>
            <IconRefresh size={15} />
          </ActionIcon>
        </Tooltip>

        <Menu position="bottom-start" withArrow>
          <Menu.Target>
            <Tooltip label="Export">
              <ActionIcon size="sm" variant="subtle" aria-label="Export diagram">
                <IconDownload size={15} />
              </ActionIcon>
            </Tooltip>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item onClick={() => downloadSvg(svg())}>SVG</Menu.Item>
            <Menu.Item onClick={() => downloadPng(svg()).catch(e => setError(e.message))}>PNG</Menu.Item>
          </Menu.Dropdown>
        </Menu>

        {loading
          ? <Loader size="xs" />
          : <Text size="xs" c="dimmed">{nodes.length} of {graph.current.nodes.length} tables</Text>}
      </Group>

      {error ? <Alert color="red" variant="light" m={4}>{error}</Alert> : null}

      <div ref={canvas} style={{ flex: 1, minHeight: 0 }}>
        {/* react-flow binds Space and Delete on the document while it is mounted, which would
            swallow those keys in the SQL editor of any other tab. */}
        <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes}
          onNodesChange={onNodesChange} onEdgesChange={onEdgesChange} fitView minZoom={0.1}
          panActivationKeyCode={null} deleteKeyCode={null} multiSelectionKeyCode={null}>
          <Background />
          <Controls showInteractive={false} />
          <MiniMap pannable zoomable
            maskColor={current.scheme === "dark" ? "rgba(0,0,0,.35)" : "rgba(255,255,255,.5)"}
            style={{ background: current.scheme === "dark" ? "#1a1b1e" : "#f8f9fa" }}
            nodeColor={current.scheme === "dark" ? "#4c6ef5" : "#adb5bd"} />
        </ReactFlow>
      </div>
    </div>
  );
}

export function DiagramPanel(props: CanvasProps) {
  // ReactFlowProvider has to sit above the hooks, and remounting on a connection change is the
  // simplest way to drop the old graph's state entirely.
  return useMemo(() => (
    <ReactFlowProvider key={props.connectionId}>
      <Canvas {...props} />
    </ReactFlowProvider>
    // eslint-disable-next-line react-hooks/exhaustive-deps
  ), [props.connectionId, props.onOpenTable]);
}
