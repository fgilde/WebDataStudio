import { useEffect, useState } from "react";
import { ActionIcon, Badge, Group, Loader, Text, TextInput, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconRefresh, IconSearch } from "@tabler/icons-react";
import { listConnections, listSchema, type Connection, type SchemaNodeDto } from "../api";
import { nodeIcon } from "./nodeIcons";

export interface ExplorerSelection { connectionId: string; node: SchemaNodeDto }

// One lazily loaded level. Children are fetched on first expand and cached until a manual refresh.
function TreeLevel({ conn, parent, depth, filter, onSelect }: {
  conn: string; parent?: string; depth: number; filter: string;
  onSelect: (s: ExplorerSelection) => void;
}) {
  const [nodes, setNodes] = useState<SchemaNodeDto[] | null>(null);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    listSchema(conn, parent)
      .then(n => { if (!cancelled) setNodes(n); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [conn, parent]);

  if (error) return <Text c="red" size="xs" pl={depth * 12 + 8}>{error}</Text>;
  if (!nodes) return <Loader size="xs" ml={depth * 12 + 8} my={4} />;

  const visible = filter
    ? nodes.filter(n => n.label.toLowerCase().includes(filter.toLowerCase()))
    : nodes;

  return (
    <>
      {visible.map(node => (
        <div key={node.ref}>
          <UnstyledButton
            w="100%" px={4} py={2}
            style={{ paddingLeft: depth * 12 + 4 }}
            onClick={() => {
              if (node.hasChildren) setOpen(o => ({ ...o, [node.ref]: !o[node.ref] }));
              onSelect({ connectionId: conn, node });
            }}>
            <Group gap={4} wrap="nowrap">
              {node.hasChildren
                ? (open[node.ref] ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />)
                : <span style={{ width: 12 }} />}
              {nodeIcon(node.kind)}
              <Text size="xs" truncate>{node.label}</Text>
            </Group>
          </UnstyledButton>
          {node.hasChildren && open[node.ref] && (
            <TreeLevel conn={conn} parent={node.ref} depth={depth + 1} filter="" onSelect={onSelect} />
          )}
        </div>
      ))}
    </>
  );
}

export function ExplorerTree({ onSelect }: { onSelect: (s: ExplorerSelection) => void }) {
  const [connections, setConnections] = useState<Connection[]>([]);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [filter, setFilter] = useState("");
  const [nonce, setNonce] = useState(0);

  useEffect(() => { listConnections().then(setConnections).catch(() => setConnections([])); }, [nonce]);

  return (
    <div style={{ height: "100%", overflow: "auto" }}>
      <Group gap={4} p={4} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="Filter" leftSection={<IconSearch size={13} />}
          value={filter} onChange={e => setFilter(e.currentTarget.value)} />
        <ActionIcon size="sm" variant="subtle" title="Refresh" onClick={() => setNonce(n => n + 1)}>
          <IconRefresh size={14} />
        </ActionIcon>
      </Group>

      {connections.map(c => (
        <div key={`${c.id}-${nonce}`}>
          <UnstyledButton w="100%" px={4} py={3} onClick={() => setOpen(o => ({ ...o, [c.id]: !o[c.id] }))}>
            <Group gap={4} wrap="nowrap">
              {open[c.id] ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
              <Text size="xs" fw={600} c={c.color ?? undefined} truncate>{c.name}</Text>
              {c.readOnly && <Badge size="xs" variant="light" color="orange">RO</Badge>}
            </Group>
          </UnstyledButton>
          {open[c.id] && <TreeLevel conn={c.id} depth={1} filter={filter} onSelect={onSelect} />}
        </div>
      ))}
    </div>
  );
}
