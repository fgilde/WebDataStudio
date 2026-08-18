import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { Text } from "@mantine/core";
import { IconKey, IconLink } from "@tabler/icons-react";
import type { DiagramNodeDto } from "../api";

export const MAX_ROWS = 18;
export type TableNodeData = { table: DiagramNodeDto; onOpen?: (table: DiagramNodeDto) => void };

export function TableNode({ data }: NodeProps<Node<TableNodeData>>) {
  const { table, onOpen } = data;

  return (
    <div style={{
      border: "1px solid var(--mantine-color-default-border)",
      borderRadius: 6,
      background: "var(--mantine-color-body)",
      fontSize: 11,
      overflow: "hidden",
      width: 220,
    }}>
      <Handle type="target" position={Position.Left}
        style={{ background: "var(--mantine-primary-color-filled)" }} />

      <div
        onDoubleClick={() => onOpen?.(table)}
        title="Double-click to open the data"
        style={{
          padding: "5px 8px", fontWeight: 600, fontSize: 12, cursor: onOpen ? "pointer" : "default",
          background: "var(--mantine-color-default-hover)",
          borderBottom: "1px solid var(--mantine-color-default-border)",
        }}>
        {table.name}
        {table.schema ? <Text span size="10px" c="dimmed"> · {table.schema}</Text> : null}
      </div>

      {/* Long tables are cut off rather than turned into a wall; the structure panel has it all. */}
      {table.columns.slice(0, MAX_ROWS).map(column => (
        <div key={column.name} style={{
          display: "flex", gap: 6, alignItems: "center", padding: "1px 8px", height: 18,
        }}>
          {column.primaryKey
            ? <IconKey size={10} />
            : column.foreignKey ? <IconLink size={10} /> : <span style={{ width: 10 }} />}
          <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
            {column.name}
          </span>
          <Text span size="10px" c="dimmed">{column.type}</Text>
        </div>
      ))}

      {table.columns.length > MAX_ROWS
        ? <Text size="10px" c="dimmed" px={8}>+{table.columns.length - MAX_ROWS} more</Text>
        : null}

      <Handle type="source" position={Position.Right}
        style={{ background: "var(--mantine-primary-color-filled)" }} />
    </div>
  );
}
