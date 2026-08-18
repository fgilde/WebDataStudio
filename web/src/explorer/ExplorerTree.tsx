import { useEffect, useMemo, useState } from "react";
import { ActionIcon, Badge, Group, Loader, Popover, Stack, Text, TextInput, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconRefresh, IconSearch } from "@tabler/icons-react";
import { listConnections, listSchema, type Connection, type SchemaNodeDto } from "../api";
import { nodeIcon } from "./nodeIcons";

export interface ExplorerSelection { connectionId: string; node: SchemaNodeDto }

export type ExplorerAction =
  | "select" | "design" | "new-table" | "open-data" | "new-query" | "copy-name" | "show-ddl"
  | "export" | "import" | "copy-table" | "rename"
  | "script-insert" | "script-update" | "script-delete" | "script-truncate" | "script-drop";

// Actions that only make sense on a real object, not on a folder.
const OBJECT_KINDS = ["Table", "View", "MaterializedView"];

const CONTEXT_ITEMS: { action: ExplorerAction; label: string }[] = [
  { action: "open-data", label: "Open data" },
  { action: "new-query", label: "New query (SELECT *)" },
  { action: "show-ddl", label: "Show DDL" },
  { action: "design", label: "Design table…" },
  { action: "new-table", label: "New table here…" },
  { action: "rename", label: "Rename…" },
  { action: "script-insert", label: "Script: INSERT" },
  { action: "script-update", label: "Script: UPDATE" },
  { action: "script-delete", label: "Script: DELETE" },
  // Destructive statements are written into a query tab rather than run from a menu: one
  // mis-click must never drop a table.
  { action: "script-truncate", label: "Script: TRUNCATE" },
  { action: "script-drop", label: "Script: DROP" },
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
  const [closedGroups, setClosedGroups] = useState<Record<string, boolean>>({});

  useEffect(() => { listConnections().then(setConnections).catch(() => setConnections([])); }, [nonce]);

  // Ungrouped connections keep their place at the top under the empty key.
  const groups = useMemo(() => {
    const map = new Map<string, Connection[]>();
    for (const connection of connections) {
      const key = connection.group ?? "";
      if (!map.has(key)) map.set(key, []);
      map.get(key)!.push(connection);
    }
    return [...map.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [connections]);

  return (
    <div style={{ height: "100%", overflow: "auto" }}>
      <Group gap={4} p={4} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="Filter" leftSection={<IconSearch size={13} />}
          value={filter} onChange={e => setFilter(e.currentTarget.value)} />
        <ActionIcon size="sm" variant="subtle" aria-label="Refresh explorer" onClick={() => setNonce(n => n + 1)}>
          <IconRefresh size={14} />
        </ActionIcon>
      </Group>

      {groups.map(([group, list]) => (
        <div key={group}>
          {group ? (
            <UnstyledButton w="100%" px={4} py={2}
              onClick={() => setClosedGroups(g => ({ ...g, [group]: !g[group] }))}>
              <Group gap={4} wrap="nowrap">
                {closedGroups[group] ? <IconChevronRight size={11} /> : <IconChevronDown size={11} />}
                <Text size="10px" fw={700} c="dimmed" tt="uppercase">{group}</Text>
              </Group>
            </UnstyledButton>
          ) : null}

          {!closedGroups[group] && list.map(c => (
            <div key={`${c.id}-${nonce}`}>
              <UnstyledButton w="100%" px={4} py={3} pl={group ? 14 : 4}
                onClick={() => setOpen(o => ({ ...o, [c.id]: !o[c.id] }))}
                // The colour tint is the production-is-red affordance: a wrong-window DELETE is
                // much less likely when the whole row is the wrong colour.
                style={c.color ? {
                  borderLeft: `3px solid ${c.color}`,
                  background: `color-mix(in srgb, ${c.color} 12%, transparent)`,
                } : undefined}>
                <Group gap={4} wrap="nowrap">
                  {open[c.id] ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
                  <Text size="xs" fw={600} c={c.color ?? undefined} truncate>{c.name}</Text>
                  {c.readOnly && <Badge size="xs" variant="light" color="orange">RO</Badge>}
                  {c.tunnelled && <Badge size="xs" variant="light" color="blue">SSH</Badge>}
                </Group>
              </UnstyledButton>
              {open[c.id] && (
                <TreeLevel conn={c.id} depth={1} filter={filter} onSelect={onSelect} onAction={onAction} />
              )}
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}
