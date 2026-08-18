import { useEffect, useState } from "react";
import { ActionIcon, Badge, Group, Loader, Popover, Stack, Text, TextInput, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconRefresh, IconSearch } from "@tabler/icons-react";
import { listConnections, listSchema, type Connection, type SchemaNodeDto } from "../api";
import { nodeIcon } from "./nodeIcons";

export interface ExplorerSelection { connectionId: string; node: SchemaNodeDto }

export type ExplorerAction = "select" | "open-data" | "new-query" | "copy-name" | "show-ddl" | "export" | "import" | "copy-table";

// Actions that only make sense on a real object, not on a folder.
const OBJECT_KINDS = ["Table", "View", "MaterializedView"];

const CONTEXT_ITEMS: { action: ExplorerAction; label: string }[] = [
  { action: "open-data", label: "Open data" },
  { action: "new-query", label: "New query (SELECT *)" },
  { action: "show-ddl", label: "Show DDL" },
  { action: "copy-name", label: "Copy name" },
  { action: "export", label: "Export…" },
  { action: "import", label: "Import into this table…" },
  { action: "copy-table", label: "Copy to another connection…" },
];

// One lazily loaded level. Children are fetched on first expand and cached until a manual refresh.
function TreeLevel({ conn, parent, depth, filter, onSelect, onAction }: {
  conn: string; parent?: string; depth: number; filter: string;
  onSelect: (s: ExplorerSelection) => void;
  onAction: (action: ExplorerAction, s: ExplorerSelection) => void;
}) {
  const [nodes, setNodes] = useState<SchemaNodeDto[] | null>(null);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);
  // Controlled, because an uncontrolled Menu would swallow the left click that navigates.
  const [menuFor, setMenuFor] = useState<string | null>(null);

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
          <Popover withinPortal shadow="md" position="right-start" opened={menuFor === node.ref}
            onDismiss={() => setMenuFor(null)}>
            {/* Popover, not Menu: Mantine's Menu.Target toggles on left click, which would fight
                the click that selects a node. Here the menu opens on right click only. */}
            <Popover.Target>
              <UnstyledButton
                w="100%" px={4} py={2}
                style={{ paddingLeft: depth * 12 + 4 }}
                onClick={() => {
                  if (node.hasChildren) setOpen(o => ({ ...o, [node.ref]: !o[node.ref] }));
                  onSelect({ connectionId: conn, node });
                }}
                onDoubleClick={() => {
                  if (OBJECT_KINDS.includes(node.kind)) onAction("open-data", { connectionId: conn, node });
                }}
                onContextMenu={e => {
                  if (!OBJECT_KINDS.includes(node.kind)) return;
                  e.preventDefault();
                  onSelect({ connectionId: conn, node });
                  setMenuFor(node.ref);
                }}>
                <Group gap={4} wrap="nowrap">
                  {node.hasChildren
                    ? (open[node.ref] ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />)
                    : <span style={{ width: 12 }} />}
                  {nodeIcon(node.kind)}
                  <Text size="xs" truncate>{node.label}</Text>
                </Group>
              </UnstyledButton>
            </Popover.Target>

            <Popover.Dropdown p={4}>
              <Stack gap={0}>
                {CONTEXT_ITEMS.map(item => (
                  <UnstyledButton key={item.action} px={8} py={4}
                    onClick={() => { setMenuFor(null); onAction(item.action, { connectionId: conn, node }); }}
                    style={{ borderRadius: 4 }}>
                    <Text size="xs">{item.label}</Text>
                  </UnstyledButton>
                ))}
              </Stack>
            </Popover.Dropdown>
          </Popover>

          {node.hasChildren && open[node.ref] && (
            <TreeLevel conn={conn} parent={node.ref} depth={depth + 1} filter=""
              onSelect={onSelect} onAction={onAction} />
          )}
        </div>
      ))}
    </>
  );
}

export function ExplorerTree({ onSelect, onAction }: {
  onSelect: (s: ExplorerSelection) => void;
  onAction: (action: ExplorerAction, s: ExplorerSelection) => void;
}) {
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
        <ActionIcon size="sm" variant="subtle" aria-label="Refresh explorer" onClick={() => setNonce(n => n + 1)}>
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
          {open[c.id] && <TreeLevel conn={c.id} depth={1} filter={filter} onSelect={onSelect} onAction={onAction} />}
        </div>
      ))}
    </div>
  );
}
