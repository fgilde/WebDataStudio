import { useEffect, useMemo, useState } from "react";
import {
  ActionIcon, Badge, Group, Loader, Popover, Stack, Text, TextInput, UnstyledButton,
} from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconRefresh, IconSearch } from "@tabler/icons-react";
import { listConnections, listDrivers, listSchema, type Connection, type SchemaNodeDto } from "../api";
import { nodeIcon } from "./nodeIcons";
import { ObjectSearch } from "./ObjectSearch";
import { HealthDot } from "./HealthDot";
import { schemaCache } from "../editor/schemaCache";
import {
  actionsFor, connectionActions, type ContextItem, type ExplorerAction, type MenuCapabilities,
} from "./contextActions";
import { dragHasFiles, dropKindFor, filesOf, type DropKind } from "./dropTarget";

export type { ExplorerAction } from "./contextActions";

export interface ExplorerSelection { connectionId: string; node: SchemaNodeDto }

/// One step per level. A small step makes a deep tree read as one flat list; the guide line does
/// the rest of the work.
const INDENT = 16;

// Actions that only make sense on a real object, not on a folder.
// A double-click opens the rows. A file in a bucket belongs here too: it is a table that happens
// to live somewhere else.
const OBJECT_KINDS = ["Table", "View", "MaterializedView", "StorageObject"];

/// The menu opens where the pointer is. Anchoring it to the row would put it at the row's right
/// edge — the full width of the explorer away from the click that asked for it.
function ContextMenu({ items, at, onClose, onPick }: {
  items: ContextItem[];
  at: { x: number; y: number } | null;
  onClose: () => void;
  onPick: (action: ExplorerAction) => void;
}) {
  if (!at) return null;

  return (
    <Popover withinPortal shadow="md" position="bottom-start" offset={0} opened onDismiss={onClose}>
      {/* A one-pixel anchor at the pointer, so Popover keeps its flipping and dismiss handling. */}
      <Popover.Target>
        <div style={{ position: "fixed", left: at.x, top: at.y, width: 1, height: 1 }} />
      </Popover.Target>

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
function TreeLevel({ conn, parent, depth, caps, refresh, onSelect, onAction, onDropFiles }: {
  conn: string; parent?: string; depth: number; caps: MenuCapabilities;
  /// Bumped when something changed the database. Every open level reloads; which levels are open
  /// stays exactly as it was, because a tree that collapses itself after every change is a tree
  /// somebody has to walk down again each time.
  refresh: number;
  onSelect: (s: ExplorerSelection) => void;
  onAction: (action: ExplorerAction, s: ExplorerSelection) => void;
  /// Files dropped on a node, and what that node makes of them.
  onDropFiles?: (kind: DropKind, s: ExplorerSelection, files: File[]) => void;
}) {
  const [nodes, setNodes] = useState<SchemaNodeDto[] | null>(null);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  // Controlled, because an uncontrolled menu would swallow the left click that navigates. The
  // pointer position rides along: the menu opens where the click was.
  const [menuFor, setMenuFor] = useState<{ ref: string; x: number; y: number } | null>(null);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    listSchema(conn, parent)
      .then(n => { if (!cancelled) setNodes(n); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [conn, parent, nonce, refresh]);

  // Which node a file is hovering over, so only that one lights up. Every hook belongs above the
  // early returns below: a hook that runs only sometimes is React error #310.
  const [dropOn, setDropOn] = useState<string | null>(null);

  const pad = depth * INDENT;

  if (error) return <Text c="red" size="xs" pl={pad + 8}>{error}</Text>;
  if (!nodes) return <Loader size="xs" ml={pad + 8} my={4} />;

  // The box searches objects through ObjectSearch rather than filtering here: this level is
  // schemas, folders or database numbers depending on the engine — never the tables somebody is
  // typing the name of.
  const visible = nodes;

  const act = (action: ExplorerAction, node: SchemaNodeDto) => {
    // Refreshing is this level's own business; everything else goes up to the shell.
    if (action === "refresh") { setNodes(null); setNonce(n => n + 1); return; }
    onAction(action, { connectionId: conn, node });
  };

  return (
    <>
      {visible.map(node => (
        <div key={node.ref}>
          <UnstyledButton
            w="100%" py={2}
            style={{
              paddingLeft: pad + 4,
              paddingRight: 4,
              // The node a file would land in, while it is being dragged over it.
              outline: dropOn === node.ref ? "1px dashed var(--mantine-color-blue-5)" : undefined,
              background: dropOn === node.ref
                ? "var(--mantine-color-blue-light)"
                : undefined,
            }}
            onDragOver={e => {
              if (!dragHasFiles(e.dataTransfer) || dropKindFor(node.kind) === null) return;

              // Only a node that can take the file: everything else keeps the browser's "no".
              e.preventDefault();
              e.dataTransfer.dropEffect = "copy";
              if (dropOn !== node.ref) setDropOn(node.ref);
            }}
            onDragLeave={() => setDropOn(current => (current === node.ref ? null : current))}
            onDrop={e => {
              const kind = dropKindFor(node.kind);
              const files = filesOf(e.dataTransfer).filter(file => file.size > 0 || file.name);

              setDropOn(null);
              if (kind === null || files.length === 0) return;

              e.preventDefault();
              onSelect({ connectionId: conn, node });
              onDropFiles?.(kind, { connectionId: conn, node }, files);
            }}
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
              setMenuFor({ ref: node.ref, x: e.clientX, y: e.clientY });
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

          {node.hasChildren && open[node.ref] && (
            // A guide line keeps the nesting visible where long labels push the indentation out
            // of sight. The child level starts at depth 0 again inside it.
            <div style={{
              marginLeft: pad + 10,
              borderLeft: "1px solid var(--mantine-color-default-border)",
            }}>
              <TreeLevel conn={conn} parent={node.ref} depth={1} caps={caps} refresh={refresh}
                onSelect={onSelect} onAction={onAction} onDropFiles={onDropFiles} />
            </div>
          )}
        </div>
      ))}

      {/* One menu per level, not per row: it is opened for whichever node was right-clicked. */}
      <ContextMenu at={menuFor} onClose={() => setMenuFor(null)}
        items={actionsFor(visible.find(n => n.ref === menuFor?.ref)?.kind ?? "Table", caps)}
        onPick={action => {
          const node = visible.find(n => n.ref === menuFor?.ref);
          if (node) act(action, node);
        }} />
    </>
  );
}

export function ExplorerTree({ refresh = 0, onSelect, onAction, onDropFiles }: {
  /// Bumped by the shell when a change was applied. The tree reloads what is open rather than
  /// starting over: an applied statement used to collapse everything somebody had opened.
  refresh?: number;
  onSelect: (s: ExplorerSelection) => void;
  onAction: (action: ExplorerAction, s: ExplorerSelection) => void;
  /// A file dragged onto a node: into a bucket folder as an upload, into a table as rows, into a
  /// schema as a new table. The tree knows what every node is, so this needs no dialog to ask.
  onDropFiles?: (kind: DropKind, s: ExplorerSelection, files: File[]) => void;
}) {
  const [connections, setConnections] = useState<Connection[]>([]);
  const [capsByEngine, setCapsByEngine] = useState<Record<string, MenuCapabilities>>({});
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [filter, setFilter] = useState("");
  const [nonce, setNonce] = useState(0);
  const [closedGroups, setClosedGroups] = useState<Record<string, boolean>>({});
  const [menuFor, setMenuFor] = useState<{ id: string; x: number; y: number } | null>(null);
  const [searchMenu, setSearchMenu] = useState<
    { selection: ExplorerSelection; x: number; y: number } | null>(null);

  // Two characters: one is a typo waiting to happen and would search the whole server for "a".
  const searching = filter.trim().length >= 2;

  useEffect(() => { listConnections().then(setConnections).catch(() => setConnections([])); },
    [nonce, refresh]);

  // What each engine can do decides which menu items exist at all.
  useEffect(() => {
    listDrivers()
      .then(drivers => setCapsByEngine(Object.fromEntries(drivers.map(driver => [
        driver.info.id,
        {
          ddl: driver.caps.ddl,
          multiDatabase: driver.caps.multiDatabase,
          fullTextIndexes: driver.caps.fullTextIndexes,
          browseContainers: driver.caps.browseContainers,
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
        <TextInput size="xs" flex={1} placeholder="Search tables and views"
          leftSection={<IconSearch size={13} />}
          value={filter} onChange={e => setFilter(e.currentTarget.value)} />
        <ActionIcon size="sm" variant="subtle" aria-label="Refresh explorer" onClick={() => {
          // The search reads the same cache the editor's completion does, so a refresh has to drop
          // it as well — otherwise the flat list keeps answering with objects that were dropped.
          for (const connection of connections) schemaCache.invalidate(connection.id);
          setNonce(n => n + 1);
        }}>
          <IconRefresh size={14} />
        </ActionIcon>
      </Group>

      {searching ? (
        <ObjectSearch connections={connections} filter={filter.trim()}
          onSelect={onSelect} onAction={onAction}
          onContextMenu={(selection, x, y) => setSearchMenu({ selection, x, y })} />
      ) : groups.map(([group, list]) => (
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
            <div key={c.id}>
              <UnstyledButton w="100%" px={4} py={3} pl={group ? 14 : 4}
                onClick={() => {
                  // Selecting it too, not just expanding: the toolbar's buttons act on "the
                  // current connection", and clicking one is how a user says which that is.
                  setOpen(o => ({ ...o, [c.id]: !o[c.id] }));
                  onSelect({ connectionId: c.id, node: rootNode(c) });
                }}
                onContextMenu={e => {
                  e.preventDefault();
                  setMenuFor({ id: c.id, x: e.clientX, y: e.clientY });
                }}
                // The colour tint is the production-is-red affordance: a wrong-window DELETE is
                // much less likely when the whole row is the wrong colour.
                style={c.color ? {
                  borderLeft: `3px solid ${c.color}`,
                  background: `color-mix(in srgb, ${c.color} 12%, transparent)`,
                } : undefined}>
                <Group gap={4} wrap="nowrap">
                  {open[c.id] ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
                  {/* Whether the server is answering, and how far away it is. */}
                  <HealthDot id={c.id} auto={open[c.id] === true} />
                  <Text size="xs" fw={600} c={c.color ?? undefined} truncate>{c.name}</Text>
                  {c.readOnly && <Badge size="xs" variant="light" color="orange">RO</Badge>}
                  {c.tunnelled && <Badge size="xs" variant="light" color="blue">SSH</Badge>}
                </Group>
              </UnstyledButton>

              {open[c.id] && (
                <div style={{
                  marginLeft: 10,
                  borderLeft: "1px solid var(--mantine-color-default-border)",
                }}>
                  <TreeLevel conn={c.id} depth={1} caps={capsByEngine[c.engine] ?? {}}
                    refresh={nonce + refresh}
                    onSelect={onSelect} onAction={onAction} onDropFiles={onDropFiles} />
                </div>
              )}
            </div>
          ))}
        </div>
      ))}

      {/* A search result is an object, so it gets the same menu it would have in the tree. */}
      <ContextMenu at={searchMenu} onClose={() => setSearchMenu(null)}
        items={actionsFor(searchMenu?.selection.node.kind ?? "Table",
          capsByEngine[connections.find(c => c.id === searchMenu?.selection.connectionId)?.engine ?? ""] ?? {})}
        onPick={action => {
          if (!searchMenu) return;
          if (action === "refresh") { setNonce(n => n + 1); return; }
          onAction(action, searchMenu.selection);
        }} />

      <ContextMenu at={menuFor} onClose={() => setMenuFor(null)}
        items={connectionActions(
          capsByEngine[connections.find(c => c.id === menuFor?.id)?.engine ?? ""] ?? {})}
        onPick={action => {
          const connection = connections.find(c => c.id === menuFor?.id);
          if (!connection) return;
          if (action === "refresh") { setNonce(n => n + 1); return; }
          onAction(action, { connectionId: connection.id, node: rootNode(connection) });
        }} />
    </div>
  );
}
