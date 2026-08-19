import { useEffect, useMemo, useState } from "react";
import {
  ActionIcon, Badge, Group, Loader, Popover, Stack, Text, TextInput, UnstyledButton,
} from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconRefresh, IconSearch } from "@tabler/icons-react";
import { listConnections, listDrivers, listSchema, type Connection, type SchemaNodeDto } from "../api";
import { nodeIcon } from "./nodeIcons";
import {
  actionsFor, connectionActions, type ContextItem, type ExplorerAction, type MenuCapabilities,
} from "./contextActions";

export type { ExplorerAction } from "./contextActions";

export interface ExplorerSelection { connectionId: string; node: SchemaNodeDto }

/// One step per level. A small step makes a deep tree read as one flat list; the guide line does
/// the rest of the work.
const INDENT = 16;

// Actions that only make sense on a real object, not on a folder.
const OBJECT_KINDS = ["Table", "View", "MaterializedView"];

function ContextMenu({ items, opened, onClose, onPick, children }: {
  items: ContextItem[];
  opened: boolean;
  onClose: () => void;
  onPick: (action: ExplorerAction) => void;
  children: React.ReactNode;
}) {
  return (
    <Popover withinPortal shadow="md" position="right-start" opened={opened} onDismiss={onClose}>
      {/* Popover, not Menu: Mantine's Menu.Target toggles on left click, which would fight the
          click that selects a node. Here the menu opens on right click only. */}
      <Popover.Target>{children}</Popover.Target>

      <Popover.Dropdown p={4}>
        <Stack gap={0} miw={210}>
          {items.map(item => (
            <div key={item.action}>
              {item.divider ? (
                <div style={{
                  height: 1, margin: "4px 0",
                  background: "var(--mantine-color-default-border)",
                }} />
              ) : null}
              <UnstyledButton w="100%" px={8} py={4} style={{ borderRadius: 4 }}
                onClick={() => { onClose(); onPick(item.action); }}>
                <Text size="xs" c={item.danger ? "red" : undefined}>{item.label}</Text>
              </UnstyledButton>
            </div>
          ))}
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}

// One lazily loaded level. Children are fetched on first expand and cached until a refresh.
function TreeLevel({ conn, parent, depth, filter, caps, onSelect, onAction }: {
  conn: string; parent?: string; depth: number; filter: string; caps: MenuCapabilities;
  onSelect: (s: ExplorerSelection) => void;
  onAction: (action: ExplorerAction, s: ExplorerSelection) => void;
}) {
  const [nodes, setNodes] = useState<SchemaNodeDto[] | null>(null);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  // Controlled, because an uncontrolled Menu would swallow the left click that navigates.
  const [menuFor, setMenuFor] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    listSchema(conn, parent)
      .then(n => { if (!cancelled) setNodes(n); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [conn, parent, nonce]);

  const pad = depth * INDENT;

  if (error) return <Text c="red" size="xs" pl={pad + 8}>{error}</Text>;
  if (!nodes) return <Loader size="xs" ml={pad + 8} my={4} />;

  const visible = filter
    ? nodes.filter(n => n.label.toLowerCase().includes(filter.toLowerCase()))
    : nodes;

  const act = (action: ExplorerAction, node: SchemaNodeDto) => {
    // Refreshing is this level's own business; everything else goes up to the shell.
    if (action === "refresh") { setNodes(null); setNonce(n => n + 1); return; }
    onAction(action, { connectionId: conn, node });
  };

  return (
    <>
      {visible.map(node => (
        <div key={node.ref}>
          <ContextMenu items={actionsFor(node.kind, caps)} opened={menuFor === node.ref}
            onClose={() => setMenuFor(null)} onPick={action => act(action, node)}>
            <UnstyledButton
              w="100%" py={2}
              style={{ paddingLeft: pad + 4, paddingRight: 4 }}
              onClick={() => {
                if (node.hasChildren) setOpen(o => ({ ...o, [node.ref]: !o[node.ref] }));
                onSelect({ connectionId: conn, node });
              }}
              onDoubleClick={() => {
                if (OBJECT_KINDS.includes(node.kind)) onAction("open-data", { connectionId: conn, node });
              }}
              onContextMenu={e => {
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
                {node.detail
                  ? <Text size="10px" c="dimmed" truncate>{node.detail}</Text>
                  : null}
              </Group>
            </UnstyledButton>
          </ContextMenu>

          {node.hasChildren && open[node.ref] && (
            // A guide line keeps the nesting visible where long labels push the indentation out
            // of sight. The child level starts at depth 0 again inside it.
            <div style={{
              marginLeft: pad + 10,
              borderLeft: "1px solid var(--mantine-color-default-border)",
            }}>
              <TreeLevel conn={conn} parent={node.ref} depth={1} filter="" caps={caps}
                onSelect={onSelect} onAction={onAction} />
            </div>
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
  const [capsByEngine, setCapsByEngine] = useState<Record<string, MenuCapabilities>>({});
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [filter, setFilter] = useState("");
  const [nonce, setNonce] = useState(0);
  const [closedGroups, setClosedGroups] = useState<Record<string, boolean>>({});
  const [menuFor, setMenuFor] = useState<string | null>(null);

  useEffect(() => { listConnections().then(setConnections).catch(() => setConnections([])); }, [nonce]);

  // What each engine can do decides which menu items exist at all.
  useEffect(() => {
    listDrivers()
      .then(drivers => setCapsByEngine(Object.fromEntries(drivers.map(driver => [
        driver.info.id,
        {
          ddl: driver.caps.ddl,
          multiDatabase: driver.caps.multiDatabase,
          fullTextIndexes: driver.caps.fullTextIndexes,
        } satisfies MenuCapabilities,
      ]))))
      .catch(() => setCapsByEngine({}));
  }, []);

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

  // The connection's own row is not a schema node, but an action needs something to act on.
  const rootNode = (connection: Connection): SchemaNodeDto => ({
    ref: `Database:${connection.name}`, kind: "Database", label: connection.name,
    hasChildren: true, detail: null,
  });

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
              <ContextMenu items={connectionActions(capsByEngine[c.engine] ?? {})}
                opened={menuFor === c.id} onClose={() => setMenuFor(null)}
                onPick={action => action === "refresh"
                  ? setNonce(n => n + 1)
                  : onAction(action, { connectionId: c.id, node: rootNode(c) })}>
                <UnstyledButton w="100%" px={4} py={3} pl={group ? 14 : 4}
                  onClick={() => setOpen(o => ({ ...o, [c.id]: !o[c.id] }))}
                  onContextMenu={e => { e.preventDefault(); setMenuFor(c.id); }}
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
              </ContextMenu>

              {open[c.id] && (
                <div style={{
                  marginLeft: 10,
                  borderLeft: "1px solid var(--mantine-color-default-border)",
                }}>
                  <TreeLevel conn={c.id} depth={1} filter={filter} caps={capsByEngine[c.engine] ?? {}}
                    onSelect={onSelect} onAction={onAction} />
                </div>
              )}
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}
