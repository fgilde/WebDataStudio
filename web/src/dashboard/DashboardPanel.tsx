import {
  ActionIcon, Alert, Button, Group, Loader, Modal, NumberInput, Select, Stack, Table, Text,
  TextInput, Tooltip,
} from "@mantine/core";
import { IconPlus, IconRefresh, IconTrash, IconPencil, IconExternalLink } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  deleteDashboard, listDashboards, saveDashboard, listConnections,
  type Connection, type DashboardDto, type DashboardTileDto,
} from "../api";
import { runQuery } from "../query/runQuery";
import { applyChunk, createResultState, type ResultState } from "../query/resultStore";
import { notifications } from "@mantine/notifications";

const VIEWS = [
  { value: "number", label: "one number" },
  { value: "table", label: "a table" },
  { value: "chart", label: "a bar per row" },
];

/// A page of statements, side by side.
///
/// Every piece of this existed already — saved queries, charts, the watch interval — with no place
/// that put them next to each other. This is that place and nothing more: each tile runs through the
/// same query endpoint as a query tab, with the same row cap, the same masking and the same audit
/// line. Nothing here can do what a query tab cannot.
export function DashboardPanel({ onOpenInEditor }: {
  onOpenInEditor?: (connectionId: string, sql: string) => void;
}) {
  const [dashboards, setDashboards] = useState<DashboardDto[] | null>(null);
  const [available, setAvailable] = useState(true);
  const [current, setCurrent] = useState<string | null>(null);
  const [editing, setEditing] = useState<DashboardDto | null>(null);
  const [connections, setConnections] = useState<Connection[]>([]);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    listDashboards()
      .then(state => {
        setAvailable(state.available);
        setDashboards(state.dashboards);
        setCurrent(one => one ?? state.dashboards[0]?.id ?? null);
      })
      .catch(() => setDashboards([]));

    listConnections().then(setConnections).catch(() => setConnections([]));
  }, [nonce]);

  const dashboard = dashboards?.find(one => one.id === current) ?? null;

  const remove = async (id: string) => {
    await deleteDashboard(id).catch(e => notifications.show({ color: "red", message: e.message }));
    setCurrent(null);
    setNonce(n => n + 1);
  };

  if (!dashboards) return <Loader size="xs" m="sm" />;

  if (!available)
    return (
      <Alert color="gray" variant="light" m="xs" p={8}>
        <Text size="xs">
          This studio has no workspace file, so it cannot keep a dashboard. Give it one with
          <code> DB_PATH</code>.
        </Text>
      </Alert>
    );

  return (
    <Stack gap="xs" p="xs" style={{ height: "100%", minHeight: 0 }}>
      <Group gap={6}>
        <Select size="xs" w={220} placeholder="no dashboard yet" allowDeselect={false}
          aria-label="Dashboard"
          data={dashboards.map(one => ({ value: one.id, label: one.name }))}
          value={current} onChange={setCurrent} />

        <Button size="compact-xs" leftSection={<IconPlus size={13} />}
          onClick={() => setEditing({ id: "", name: "", tiles: [], refreshSeconds: 0, updatedAt: "" })}>
          New
        </Button>

        {dashboard && (
          <>
            <ActionIcon size="sm" variant="subtle" aria-label="Edit this dashboard"
              onClick={() => setEditing(dashboard)}>
              <IconPencil size={14} />
            </ActionIcon>
            <ActionIcon size="sm" variant="subtle" aria-label="Reload the tiles"
              onClick={() => setNonce(n => n + 1)}>
              <IconRefresh size={14} />
            </ActionIcon>
            <ActionIcon size="sm" variant="subtle" color="red" aria-label="Delete this dashboard"
              onClick={() => remove(dashboard.id)}>
              <IconTrash size={14} />
            </ActionIcon>

            {dashboard.refreshSeconds > 0 && (
              <Text size="xs" c="dimmed">every {dashboard.refreshSeconds}s</Text>
            )}
          </>
        )}
      </Group>

      {!dashboard && (
        <Text size="xs" c="dimmed">
          A dashboard is a page of statements: the number somebody asks for every morning, the table
          they check after a deployment. Make one, and it runs itself.
        </Text>
      )}

      {dashboard && (
        <div style={{
          display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 8,
          overflow: "auto", flex: 1, minHeight: 0, alignContent: "start",
        }}>
          {dashboard.tiles.map((tile, index) => (
            <Tile key={`${tile.title}-${index}`} tile={tile} nonce={nonce}
              refreshSeconds={dashboard.refreshSeconds} onOpenInEditor={onOpenInEditor} />
          ))}
        </div>
      )}

      <DashboardEditor dashboard={editing} connections={connections}
        onClose={() => setEditing(null)}
        onSaved={saved => {
          setEditing(null);
          setCurrent(saved.id);
          setNonce(n => n + 1);
        }} />
    </Stack>
  );
}

/// One box: its statement runs on mount, on the dashboard's interval, and on a reload.
function Tile({ tile, refreshSeconds, nonce, onOpenInEditor }: {
  tile: DashboardTileDto;
  refreshSeconds: number;
  nonce: number;
  onOpenInEditor?: (connectionId: string, sql: string) => void;
}) {
  const [result, setResult] = useState<ResultState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const running = useRef(false);

  const run = useCallback(async () => {
    // One run at a time: a tile whose query takes longer than the interval must not pile up.
    if (running.current) return;

    running.current = true;
    setError(null);

    let state = createResultState();
    const active = runQuery({ connectionId: tile.connectionId, sql: tile.sql, maxRows: 200 },
      chunk => { state = applyChunk(state, chunk); });

    try {
      await active.done;
      setResult(state);

      const failed = state.statements.find(statement => statement.error);
      if (failed?.error) setError(failed.error.text);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      running.current = false;
    }
  }, [tile.connectionId, tile.sql]);

  useEffect(() => { void run(); }, [run, nonce]);

  useEffect(() => {
    if (refreshSeconds <= 0) return;

    const timer = window.setInterval(() => void run(), refreshSeconds * 1000);
    return () => window.clearInterval(timer);
  }, [refreshSeconds, run]);

  const first = result?.statements[0];
  const rows = first?.rows ?? [];

  return (
    <div style={{
      gridColumn: `span ${Math.min(tile.width, 4)}`,
      border: "1px solid var(--mantine-color-default-border)", borderRadius: 6,
      padding: 8, minHeight: 96, overflow: "hidden",
    }}>
      <Group gap={4} justify="space-between" wrap="nowrap">
        <Text size="xs" fw={600} truncate>{tile.title}</Text>
        <Tooltip label="Open this statement in a query tab">
          <ActionIcon size="xs" variant="subtle" aria-label={`Open ${tile.title}`}
            onClick={() => onOpenInEditor?.(tile.connectionId, tile.sql)}>
            <IconExternalLink size={12} />
          </ActionIcon>
        </Tooltip>
      </Group>

      {error && <Text size="xs" c="red" mt={4}>{error}</Text>}

      {!error && !result && <Loader size="xs" mt={6} />}

      {!error && result && tile.view === "number" && (
        <Text size="28px" fw={700} mt={4}>
          {rows.length === 0 ? "—" : String(rows[0][0] ?? "—")}
        </Text>
      )}

      {!error && result && tile.view === "table" && (
        <Table fz="xs" mt={4}>
          <Table.Tbody>
            {rows.slice(0, 8).map((row, index) => (
              <Table.Tr key={index}>
                {row.slice(0, 4).map((cell, cellIndex) => (
                  <Table.Td key={cellIndex}>{String(cell ?? "")}</Table.Td>
                ))}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      {!error && result && tile.view === "chart" && <Bars rows={rows} />}
    </div>
  );
}

/// A bar per row: the first column names it, the last number in the row is its length. Deliberately
/// not a chart library — a dashboard tile is read at a glance from across a room.
function Bars({ rows }: { rows: unknown[][] }) {
  const data = useMemo(() => rows.slice(0, 12).map(row => ({
    label: String(row[0] ?? ""),
    value: Number(row.find((cell, index) => index > 0 && typeof cell === "number") ?? 0),
  })), [rows]);

  const largest = Math.max(1, ...data.map(one => Math.abs(one.value)));

  return (
    <Stack gap={2} mt={6}>
      {data.map((one, index) => (
        <Group key={`${one.label}-${index}`} gap={6} wrap="nowrap">
          <Text size="10px" style={{ width: 90 }} truncate>{one.label}</Text>
          <div style={{
            height: 10, borderRadius: 2, background: "var(--mantine-primary-color-filled)",
            width: `${Math.max(2, (Math.abs(one.value) / largest) * 100)}%`,
          }} />
          <Text size="10px" c="dimmed">{one.value}</Text>
        </Group>
      ))}
    </Stack>
  );
}

/// The page itself: a name, how often it runs, and the tiles.
function DashboardEditor({ dashboard, connections, onClose, onSaved }: {
  dashboard: DashboardDto | null;
  connections: Connection[];
  onClose: () => void;
  onSaved: (saved: DashboardDto) => void;
}) {
  const [name, setName] = useState("");
  const [refresh, setRefresh] = useState<number | string>(0);
  const [tiles, setTiles] = useState<DashboardTileDto[]>([]);

  useEffect(() => {
    if (!dashboard) return;

    setName(dashboard.name);
    setRefresh(dashboard.refreshSeconds);
    setTiles(dashboard.tiles);
  }, [dashboard]);

  if (!dashboard) return null;

  const change = (index: number, patch: Partial<DashboardTileDto>) =>
    setTiles(list => list.map((tile, i) => (i === index ? { ...tile, ...patch } : tile)));

  const save = async () => {
    try {
      onSaved(await saveDashboard(dashboard.id, {
        name, tiles, refreshSeconds: Number(refresh) || 0,
      }));
    } catch (e) {
      notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) });
    }
  };

  return (
    <Modal opened onClose={onClose} size="lg" title={dashboard.id ? "Edit dashboard" : "New dashboard"}>
      <Stack gap="xs">
        <Group grow>
          <TextInput size="xs" label="Name" value={name} data-autofocus
            onChange={event => setName(event.currentTarget.value)} />
          <NumberInput size="xs" label="Run itself every" min={0} max={3600} suffix=" s"
            description="0 means only when asked" value={refresh} onChange={setRefresh} />
        </Group>

        {tiles.map((tile, index) => (
          <Stack key={index} gap={4} p={6}
            style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 4 }}>
            <Group gap={6} wrap="nowrap">
              <TextInput size="xs" style={{ flex: 1 }} placeholder="Title" value={tile.title}
                aria-label={`Title of tile ${index + 1}`}
                onChange={event => change(index, { title: event.currentTarget.value })} />
              <Select size="xs" w={130} data={VIEWS} value={tile.view} allowDeselect={false}
                aria-label={`View of tile ${index + 1}`}
                onChange={value => change(index, { view: value ?? "table" })} />
              <Select size="xs" w={140} placeholder="connection"
                aria-label={`Connection of tile ${index + 1}`}
                data={connections.map(one => ({ value: one.id, label: one.name }))}
                value={tile.connectionId || null}
                onChange={value => change(index, { connectionId: value ?? "" })} />
              <NumberInput size="xs" w={70} min={1} max={4} value={tile.width}
                aria-label={`Width of tile ${index + 1}`}
                onChange={value => change(index, { width: Number(value) || 1 })} />
              <ActionIcon size="sm" variant="subtle" color="red"
                aria-label={`Remove tile ${index + 1}`}
                onClick={() => setTiles(list => list.filter((_, i) => i !== index))}>
                <IconTrash size={14} />
              </ActionIcon>
            </Group>
            <TextInput size="xs" placeholder="SELECT count(*) FROM orders WHERE placed > now() - interval '1 day'"
              aria-label={`Statement of tile ${index + 1}`}
              value={tile.sql} onChange={event => change(index, { sql: event.currentTarget.value })} />
          </Stack>
        ))}

        <Group justify="space-between">
          <Button size="compact-xs" variant="default" leftSection={<IconPlus size={13} />}
            onClick={() => setTiles(list => [...list, {
              title: "", connectionId: connections[0]?.id ?? "", sql: "", view: "number", width: 1,
            }])}>
            Add a tile
          </Button>

          <Group gap={6}>
            <Button size="xs" variant="default" onClick={onClose}>Cancel</Button>
            <Button size="xs" onClick={save}>Save</Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
