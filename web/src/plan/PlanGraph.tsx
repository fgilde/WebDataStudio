import { memo, useEffect, useMemo } from "react";
import {
  Background, Controls, Handle, MiniMap, Position, ReactFlow, ReactFlowProvider, useReactFlow,
  type Edge, type Node, type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { Group, Text, Tooltip, useComputedColorScheme } from "@mantine/core";
import {
  IconAlertTriangle, IconArrowsSplit, IconBolt, IconCalculator, IconDatabaseEdit, IconFilter,
  IconFocus2, IconLayersIntersect, IconListSearch, IconSortDescending, IconStack2, IconSum,
  IconTable, IconTopologyStar,
} from "@tabler/icons-react";
import type { PlanNodeDto } from "../api";
import { heatColor } from "./heat";
import {
  NODE_HEIGHT, NODE_WIDTH, costShare, edgeWidth, layoutPlan, operatorFamily, ownCost,
  type OperatorFamily,
} from "./planModel";

const ICONS: Record<OperatorFamily, typeof IconTable> = {
  seek: IconFocus2, scan: IconTable, lookup: IconListSearch, join: IconLayersIntersect,
  aggregate: IconSum, sort: IconSortDescending, spool: IconStack2, compute: IconCalculator,
  filter: IconFilter, parallelism: IconArrowsSplit, write: IconDatabaseEdit, other: IconTopologyStar,
};

interface CardData extends Record<string, unknown> {
  node: PlanNodeDto; share: number | null; heat: string; selected: boolean; match: boolean;
}

const format = (n: number) => Math.round(n).toLocaleString();

const PlanCard = memo(function PlanCard({ data }: NodeProps<Node<CardData>>) {
  const { node, share, heat, selected, match } = data;
  const Icon = ICONS[operatorFamily(node.operation)];

  return (
    <div style={{
      width: NODE_WIDTH, height: NODE_HEIGHT, padding: 6, borderRadius: 8, overflow: "hidden",
      background: `linear-gradient(${heat}, ${heat}), var(--mantine-color-body)`,
      border: `${selected ? 2 : 1}px solid ${selected ? "var(--mantine-primary-color-filled)"
        : match ? "var(--mantine-color-yellow-5)" : "var(--mantine-color-default-border)"}`,
      // Selected wins over a search hit: the property grid is showing this one.
      boxShadow: selected ? "0 0 0 3px var(--mantine-primary-color-light)"
        : match ? "0 0 0 3px var(--mantine-color-yellow-3)" : undefined,
    }}>
      {/* The rows arrive from the right and leave to the left. */}
      <Handle type="target" position={Position.Right} style={{ opacity: 0 }} />
      <Handle type="source" position={Position.Left} style={{ opacity: 0 }} />
      <Group gap={4} wrap="nowrap">
        <Icon size={16} />
        <Text size="xs" fw={700} truncate style={{ flex: 1 }}>{node.operation}</Text>
        {node.warnings.length > 0 && (
          <Tooltip label={node.warnings.join("; ")} withinPortal>
            <IconAlertTriangle size={13} color="var(--mantine-color-orange-6)" />
          </Tooltip>
        )}
      </Group>
      {node.object && <Text size="10px" c="dimmed" truncate title={node.object}>{node.object}</Text>}
      <Group gap={6} mt={2} wrap="nowrap">
        {share !== null && <Text size="10px" fw={700}>{Math.round(share * 100)}%</Text>}
        {node.actualMs != null && <Text size="10px" c="dimmed"><IconBolt size={9} /> {node.actualMs.toFixed(0)} ms</Text>}
      </Group>
      <Text size="10px" c="dimmed" truncate>
        {node.actualRows != null ? `${format(node.actualRows)} of ` : ""}
        {node.estimatedRows != null ? `${format(node.estimatedRows)} est.` : ""}
      </Text>
    </div>
  );
});

const nodeTypes = { plan: PlanCard };

export function PlanGraph(props: {
  root: PlanNodeDto; statementCost: number | null; selected: string | null;
  onSelect: (id: string | null, node: PlanNodeDto | null) => void; matches: Set<string>; focus: string | null;
}) {
  return <ReactFlowProvider><Graph {...props} /></ReactFlowProvider>;
}

function Graph({ root, statementCost, selected, onSelect, matches, focus }: Parameters<typeof PlanGraph>[0]) {
  const layout = useMemo(() => layoutPlan(root), [root]);
  const maxOwn = useMemo(() => Math.max(0, ...layout.nodes.map(n => ownCost(n.node))), [layout]);
  const flow = useReactFlow();
  const scheme = useComputedColorScheme("dark");

  const nodes: Node<CardData>[] = useMemo(() => layout.nodes.map(({ id, node, position }) => ({
    id, type: "plan", position, draggable: false,
    data: {
      node, share: costShare(node, statementCost), heat: heatColor(ownCost(node), maxOwn),
      selected: id === selected, match: matches.has(id),
    },
  })), [layout, statementCost, maxOwn, selected, matches]);

  const edges: Edge[] = useMemo(() => layout.edges.map(e => ({
    id: e.id, source: e.source, target: e.target, type: "smoothstep",
    label: e.rows === null ? undefined : Math.round(e.rows).toLocaleString(),
    labelStyle: { fontSize: 9 }, labelShowBg: false,
    style: { strokeWidth: edgeWidth(e.rows), stroke: "var(--mantine-color-dimmed)" },
  })), [layout]);

  // Search steps through matches: the one in focus is centred.
  useEffect(() => {
    const target = focus ? layout.nodes.find(n => n.id === focus) : null;
    if (target) flow.setCenter(target.position.x + NODE_WIDTH / 2, target.position.y + NODE_HEIGHT / 2, { zoom: 1, duration: 300 });
  }, [focus, layout, flow]);

  return (
    <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} fitView minZoom={0.05} colorMode={scheme}
      onlyRenderVisibleElements nodesConnectable={false} proOptions={{ hideAttribution: true }}
      onNodeClick={(_, n) => onSelect(n.id, (n.data as CardData).node)} onPaneClick={() => onSelect(null, null)}>
      <Background gap={24} size={1} color="var(--mantine-color-default-border)" />
      <Controls showInteractive={false} />
      {layout.nodes.length > 20 && (
        <MiniMap pannable zoomable bgColor="var(--mantine-color-body)" maskColor="rgba(0, 0, 0, 0.35)"
          nodeColor="var(--mantine-color-default-border)" />
      )}
    </ReactFlow>
  );
}
