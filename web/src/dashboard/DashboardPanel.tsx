import { useCallback, useEffect, useRef, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Code, Divider, Drawer, Group, Loader, Menu, Modal,
  MultiSelect, NumberInput, ScrollArea, SegmentedControl, Select, SimpleGrid, Stack, Text,
  Textarea, TextInput, Tooltip, UnstyledButton,
} from "@mantine/core";
import { notifications } from "@mantine/notifications";
import {
  IconBraces, IconCheck, IconClock, IconCopy, IconDeviceFloppy, IconDownload, IconLayoutGrid,
  IconPencil, IconPlus, IconRefresh, IconTrash, IconVariable, IconX,
} from "@tabler/icons-react";
import {
  deleteDashboard, exportDashboardToGrafana, importDashboard, listConnections, listDashboards,
  saveDashboard, type Connection,
} from "../api";
import { useMantineColorScheme } from "@mantine/core";
import { Canvas } from "./Canvas";
import { WidgetSettings } from "./WidgetSettings";
import { WidgetFrame } from "./widgets/WidgetFrame";
import {
  RANGES, WIDGET_TYPES, emptyDashboard, emptyWidget, rangeLabel, readDashboard,
  type Dashboard, type DashboardVariable, type Widget, type WidgetType,
} from "./model";
import { runQuery } from "../query/runQuery";
import { contextOf } from "./useWidgetData";

const said = (e: unknown) => (e instanceof Error ? e.message : String(e));

/// A page of widgets: a canvas in twenty-four columns, one time range, and the variables the
/// statements read.
///
/// Nothing here executes anything itself — a widget's statement runs through the same endpoint a
/// query tab runs through, or through the studio's own federation when it spans connections. What
/// this owns is the page: which dashboard, which mode, which range, which values.
export function DashboardPanel({ onOpenInEditor }: {
  onOpenInEditor?: (connectionId: string, sql: string) => void;
}) {
  const [dashboards, setDashboards] = useState<Dashboard[] | null>(null);
  const [available, setAvailable] = useState(true);
  const [current, setCurrent] = useState<string | null>(null);
  const [draft, setDraft] = useState<Dashboard | null>(null);
  const [connections, setConnections] = useState<Connection[]>([]);
  const [editing, setEditing] = useState<Widget | null>(null);
  const [chosen, setChosen] = useState<Record<string, string[]>>({});
  const [values, setValues] = useState<Record<string, string[]>>({});
  const [nonce, setNonce] = useState(0);
  const [palette, setPalette] = useState(false);
  const [json, setJson] = useState<string | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  const [variablesOpen, setVariablesOpen] = useState(false);
  const [reload, setReload] = useState(0);
  const { colorScheme } = useMantineColorScheme();
  const dark = colorScheme === "dark"
    || (colorScheme === "auto" && window.matchMedia?.("(prefers-color-scheme: dark)").matches);

  // The columns each widget's last result had, so the settings drawer offers them rather than
  // asking somebody to type a column name.
  const columns = useRef<Record<string, string[]>>({});

  useEffect(() => {
    let cancelled = false;

    listDashboards()
      .then(state => {
        if (cancelled) return;
        setAvailable(state.available);
        setDashboards(state.dashboards.map(readDashboard));
        setCurrent(one => one ?? state.dashboards[0]?.id ?? null);
      })
      .catch(() => { if (!cancelled) setDashboards([]); });

    listConnections()
      .then(list => { if (!cancelled) setConnections(list); })
      .catch(() => { if (!cancelled) setConnections([]); });

    return () => { cancelled = true; };
  }, [reload]);

  const stored = dashboards?.find(one => one.id === current) ?? null;
  const dashboard = draft ?? stored;

  // A dashboard on a wall runs itself; one nobody set an interval on runs when asked.
  useEffect(() => {
    const seconds = dashboard?.refreshSeconds ?? 0;
    if (seconds <= 0 || draft) return;

    const timer = window.setInterval(() => setNonce(n => n + 1), seconds * 1000);
    return () => window.clearInterval(timer);
  }, [dashboard?.refreshSeconds, draft]);

  // A query variable's values come from its own statement, on the connection it names.
  useEffect(() => {
    if (!dashboard) return;

    for (const variable of dashboard.variables ?? []) {
      if (variable.kind !== "Query" || !variable.connectionId || !variable.sql) continue;

      const rows: string[] = [];

      runQuery({
        connectionId: variable.connectionId,
        sql: variable.sql,
        maxRows: 1000,
        dashboard: contextOf(dashboard, {}),
      }, chunk => {
        if (chunk.type === "rows")
          rows.push(...chunk.rows.map(row => String(row[0] ?? "")).filter(one => one.length > 0));
      }).done.then(() => {
        setValues(current => ({ ...current, [variable.name]: [...new Set(rows)] }));
      });
    }
    // The variables' own definitions are what matters here, not the widgets around them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [JSON.stringify(dashboard?.variables), dashboard?.timeRange?.from, dashboard?.timeRange?.to]);

  // A variable nobody has chosen for yet stands at its default, or at everything it offers.
  useEffect(() => {
    if (!dashboard) return;

    setChosen(current => {
      const next = { ...current };

      for (const variable of dashboard.variables ?? []) {
        if (next[variable.name]) continue;

        const offered = variable.kind === "Query"
          ? values[variable.name] ?? []
          : variable.values ?? [];

        next[variable.name] = variable.default
          ? [variable.default]
          : variable.multi ? offered : offered.slice(0, 1);
      }

      return next;
    });
  }, [dashboard, values]);

  const edit = useCallback(() => {
    if (dashboard) setDraft(structuredClone(dashboard));
  }, [dashboard]);

  const change = (patch: Partial<Dashboard>) =>
    setDraft(one => (one ? { ...one, ...patch } : one));

  const changeWidget = (widget: Widget) => {
    setEditing(widget);
    setDraft(one => one
      ? { ...one, widgets: one.widgets.map(w => (w.id === widget.id ? widget : w)) }
      : one);
  };

  const add = (type: WidgetType) => {
    setPalette(false);
    setDraft(one => {
      if (!one) return one;
      // Under everything already there: a new widget must never land on top of one.
      const bottom = one.widgets.reduce((low, w) => Math.max(low, w.position.y + w.position.h), 0);
      const widget = emptyWidget(type, { y: bottom });
      setEditing(widget);
      return { ...one, widgets: [...one.widgets, widget] };
    });
  };

  const save = async () => {
    if (!draft) return;

    try {
      const kept = await saveDashboard(draft.id.startsWith("shipped:") ? "" : draft.id, draft);
      setDraft(null);
      setCurrent(kept.id);
      setReload(n => n + 1);
      notifications.show({ message: `${kept.name} saved`, color: "green" });
    } catch (e) {
      notifications.show({ message: said(e), color: "red" });
    }
  };

  const remove = async (id: string) => {
    try {
      await deleteDashboard(id);
      setCurrent(null);
      setDraft(null);
      setReload(n => n + 1);
    } catch (e) {
      notifications.show({ message: said(e), color: "red" });
    }
  };

  const paste = async (text: string) => {
    try {
      const read = await importDashboard(text);
      setDraft(readDashboard(read.dashboard));
      setNotes(read.notes);
      setJson(null);
      // Not saved: an import nobody has looked at is not a dashboard they asked to keep.
      notifications.show({
        message: read.notes.length > 0
          ? `${read.dashboard.name} imported, with ${read.notes.length} note(s)`
          : `${read.dashboard.name} imported — save it to keep it`,
      });
    } catch (e) {
      notifications.show({ message: said(e), color: "red" });
    }
  };

  const exportJson = async () => {
    if (!dashboard) return;
    try {
      setJson(await exportDashboardToGrafana(dashboard.id));
    } catch (e) {
      notifications.show({ message: said(e), color: "red" });
    }
  };

  if (!dashboards) return <Loader size="xs" m="sm" />;

  return (
    <Stack gap={0} h="100%" style={{ minHeight: 0 }}>
      <Group gap={6} p={6} wrap="wrap" style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
        <Select size="xs" w={200} searchable placeholder="Pick a dashboard"
          data={dashboards.map(one => ({
            value: one.id,
            label: one.fromFile ? `${one.name} (shipped)` : one.name,
          }))}
          value={draft ? null : current}
          disabled={draft !== null}
          // Picking the one already open must not close it: there is no "no dashboard" to pick.
          allowDeselect={false}
          onChange={value => { setCurrent(value); setNotes([]); }}
          aria-label="Dashboard" />

        {draft ? (
          <TextInput size="xs" w={180} value={draft.name} aria-label="Name"
            onChange={e => change({ name: e.currentTarget.value })} />
        ) : null}

        <Tooltip label={rangeLabel(dashboard?.timeRange)}>
          <Select size="xs" w={150} leftSection={<IconClock size={14} />}
            data={RANGES.map(one => ({ value: `${one.from}|${one.to}`, label: one.label }))}
            value={dashboard ? `${dashboard.timeRange?.from}|${dashboard.timeRange?.to}` : null}
            allowDeselect={false} aria-label="Time range"
            disabled={!dashboard}
            onChange={value => {
              if (!value || !dashboard) return;
              const [from, to] = value.split("|");
              // In view mode the range is this browser's choice, not a change to the dashboard.
              if (draft) change({ timeRange: { from, to } });
              else setDashboards(list => (list ?? []).map(one =>
                one.id === dashboard.id ? { ...one, timeRange: { from, to } } : one));
              setNonce(n => n + 1);
            }} />
        </Tooltip>

        {(dashboard?.variables ?? []).map(variable => (
          <VariableControl key={variable.name} variable={variable}
            offered={variable.kind === "Query" ? values[variable.name] ?? [] : variable.values ?? []}
            chosen={chosen[variable.name] ?? []}
            onChange={next => { setChosen(one => ({ ...one, [variable.name]: next })); setNonce(n => n + 1); }} />
        ))}

        <Group gap={2} ml="auto" wrap="nowrap">
          <Tooltip label="Run every widget again">
            <ActionIcon size="sm" variant="subtle" aria-label="Refresh"
              onClick={() => setNonce(n => n + 1)}>
              <IconRefresh size={15} />
            </ActionIcon>
          </Tooltip>

          {draft ? (
            <>
              <NumberInput size="xs" w={110} min={0} max={3600} step={30}
                aria-label="Refresh seconds" placeholder="no refresh"
                value={draft.refreshSeconds || undefined}
                onChange={value => change({ refreshSeconds: typeof value === "number" ? value : 0 })} />
              <Button size="compact-xs" variant="light" leftSection={<IconPlus size={13} />}
                onClick={() => setPalette(true)}>
                Add widget
              </Button>
              <Button size="compact-xs" variant="light" leftSection={<IconVariable size={13} />}
                onClick={() => setVariablesOpen(true)}>
                Variables
              </Button>
              <Button size="compact-xs" leftSection={<IconDeviceFloppy size={13} />} onClick={save}>
                Save
              </Button>
              <ActionIcon size="sm" variant="subtle" aria-label="Discard"
                onClick={() => { setDraft(null); setEditing(null); setNotes([]); }}>
                <IconX size={15} />
              </ActionIcon>
            </>
          ) : (
            <>
              <Tooltip label="Edit this dashboard">
                <ActionIcon size="sm" variant="subtle" aria-label="Edit" disabled={!dashboard}
                  onClick={edit}>
                  <IconPencil size={15} />
                </ActionIcon>
              </Tooltip>
              <Menu position="bottom-end" withinPortal>
                <Menu.Target>
                  <ActionIcon size="sm" variant="subtle" aria-label="More">
                    <IconBraces size={15} />
                  </ActionIcon>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item leftSection={<IconPlus size={14} />}
                    onClick={() => { setDraft(emptyDashboard()); setNotes([]); }}>
                    New dashboard
                  </Menu.Item>
                  <Menu.Item leftSection={<IconBraces size={14} />} onClick={() => setJson("")}>
                    Paste JSON (ours or Grafana's)
                  </Menu.Item>
                  <Menu.Item leftSection={<IconDownload size={14} />} disabled={!dashboard}
                    onClick={exportJson}>
                    Export as Grafana JSON
                  </Menu.Item>
                  {dashboard ? (
                    <Menu.Item leftSection={<IconCopy size={14} />}
                      onClick={() => setDraft({
                        ...structuredClone(dashboard), id: "", name: `${dashboard.name} copy`,
                        fromFile: false,
                      })}>
                      Duplicate
                    </Menu.Item>
                  ) : null}
                  {dashboard && !dashboard.fromFile ? (
                    <Menu.Item leftSection={<IconTrash size={14} />} c="red"
                      onClick={() => remove(dashboard.id)}>
                      Delete
                    </Menu.Item>
                  ) : null}
                </Menu.Dropdown>
              </Menu>
            </>
          )}
        </Group>
      </Group>

      {!available ? (
        <Alert color="gray" variant="light" m="xs" p={8}>
          <Text size="xs">
            This studio has no workspace file, so a dashboard cannot be kept. The ones the
            deployment ships still show.
          </Text>
        </Alert>
      ) : null}

      {notes.length > 0 ? (
        <Alert color="yellow" variant="light" m="xs" p={8}
          withCloseButton onClose={() => setNotes([])}
          title="What could not come along">
          <Stack gap={2}>
            {notes.map((note, index) => <Text key={index} size="xs">{note}</Text>)}
          </Stack>
        </Alert>
      ) : null}

      {dashboard?.fromFile && !draft ? (
        <Group gap={6} px="xs" py={4}>
          <Badge size="xs" variant="light">shipped with the deployment</Badge>
          <Text size="xs" c="dimmed">Duplicate it to change anything.</Text>
        </Group>
      ) : null}

      <ScrollArea flex={1} type="auto">
        {!dashboard ? (
          <Stack align="center" justify="center" gap="xs" p="xl">
            <IconLayoutGrid size={28} opacity={0.4} />
            <Text size="sm" c="dimmed">No dashboard open.</Text>
            <Button size="compact-sm" variant="light" leftSection={<IconPlus size={14} />}
              onClick={() => setDraft(emptyDashboard())}>
              New dashboard
            </Button>
          </Stack>
        ) : dashboard.widgets.length === 0 ? (
          <Stack align="center" justify="center" gap="xs" p="xl">
            <Text size="sm" c="dimmed">This dashboard has no widgets yet.</Text>
            {draft ? (
              <Button size="compact-sm" variant="light" leftSection={<IconPlus size={14} />}
                onClick={() => setPalette(true)}>
                Add widget
              </Button>
            ) : (
              <Button size="compact-sm" variant="light" leftSection={<IconPencil size={14} />}
                onClick={edit}>
                Edit
              </Button>
            )}
          </Stack>
        ) : (
          <Canvas dashboard={dashboard} editing={draft !== null}
            rowHeight={dashboard.layout?.rowHeight ?? 40}
            onLayout={positions => setDraft(one => one
              ? {
                ...one,
                widgets: one.widgets.map(widget => positions[widget.id]
                  ? { ...widget, position: positions[widget.id] }
                  : widget),
              }
              : one)}>
            {widget => (
              <WidgetFrame widget={widget} dashboard={dashboard} chosen={chosen} dark={dark}
                editing={draft !== null} nonce={nonce}
                onEdit={() => setEditing(widget)}
                onDuplicate={() => setDraft(one => one
                  ? {
                    ...one,
                    widgets: [...one.widgets, {
                      ...structuredClone(widget),
                      id: Math.random().toString(36).slice(2, 10),
                      position: { ...widget.position, y: widget.position.y + widget.position.h },
                    }],
                  }
                  : one)}
                onRemove={() => setDraft(one => one
                  ? { ...one, widgets: one.widgets.filter(w => w.id !== widget.id) }
                  : one)}
                onOpenInEditor={onOpenInEditor} />
            )}
          </Canvas>
        )}
      </ScrollArea>

      <Drawer opened={palette} onClose={() => setPalette(false)} position="right" size={340}
        padding="md" title="Add a widget">
        <Stack gap="md">
          {[...new Set(WIDGET_TYPES.map(one => one.group))].map(group => (
            <Stack key={group} gap={6}>
              <Text size="xs" fw={700} c="dimmed" tt="uppercase">{group}</Text>
              <SimpleGrid cols={2} spacing={6}>
                {WIDGET_TYPES.filter(one => one.group === group).map(one => (
                  <UnstyledButton key={one.type} onClick={() => add(one.type)}
                    style={{
                      border: "1px solid var(--mantine-color-default-border)",
                      borderRadius: 8, padding: 8,
                    }}>
                    <Text size="xs" fw={600}>{one.label}</Text>
                    <Text size="xs" c="dimmed">{one.hint}</Text>
                  </UnstyledButton>
                ))}
              </SimpleGrid>
            </Stack>
          ))}
        </Stack>
      </Drawer>

      <WidgetSettings widget={editing} connections={connections}
        columns={editing ? columns.current[editing.id] ?? columnsOf(editing) : []}
        opened={editing !== null && draft !== null}
        onChange={changeWidget} onClose={() => setEditing(null)} />

      <Variables opened={variablesOpen} dashboard={draft} connections={connections}
        onChange={variables => change({ variables })} onClose={() => setVariablesOpen(false)} />

      <Modal opened={json !== null} onClose={() => setJson(null)} size="xl" title="Dashboard JSON">
        <Stack gap="sm">
          <Text size="xs" c="dimmed">
            Ours, the old tile shape or a Grafana dashboard — nothing has to say which. What cannot
            come along is answered as notes rather than swallowed.
          </Text>
          <Textarea autosize minRows={14} maxRows={22} value={json ?? ""}
            styles={{ input: { fontFamily: "monospace", fontSize: 11 } }}
            aria-label="The dashboard as JSON"
            onChange={e => setJson(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setJson(null)}>Close</Button>
            <Button leftSection={<IconCheck size={14} />} disabled={!json?.trim()}
              onClick={() => paste(json!)}>
              Import
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

/// The columns a widget's own statement names, as a last resort for the settings drawer before the
/// widget has ever run: the words after SELECT are usually right enough to pick from.
const columnsOf = (widget: Widget): string[] => {
  const match = /select\s+([\s\S]*?)\s+from\s/i.exec(widget.source.sql ?? "");
  if (!match) return [];

  return match[1].split(",")
    .map(one => one.trim().split(/\s+as\s+|\s+/i).pop() ?? "")
    .map(one => one.replace(/["'`[\]]/g, ""))
    .filter(one => one.length > 0 && one !== "*");
};

function VariableControl({ variable, offered, chosen, onChange }: {
  variable: DashboardVariable;
  offered: string[];
  chosen: string[];
  onChange: (values: string[]) => void;
}) {
  const label = variable.label || variable.name;

  if (variable.kind === "Constant")
    return (
      <TextInput size="xs" w={140} label={undefined} placeholder={label} aria-label={label}
        value={chosen[0] ?? ""} onChange={e => onChange([e.currentTarget.value])} />
    );

  const data = variable.includeAll ? ["$__all", ...offered] : offered;

  return variable.multi ? (
    <MultiSelect size="xs" w={200} data={data} value={chosen} aria-label={label}
      placeholder={label} searchable clearable
      onChange={values => onChange(values.includes("$__all") ? offered : values)} />
  ) : (
    <Select size="xs" w={160} data={data} value={chosen[0] ?? null} aria-label={label}
      placeholder={label} searchable
      onChange={value => onChange(value === "$__all" ? offered : value ? [value] : [])} />
  );
}

function Variables({ opened, dashboard, connections, onChange, onClose }: {
  opened: boolean;
  dashboard: Dashboard | null;
  connections: Connection[];
  onChange: (variables: DashboardVariable[]) => void;
  onClose: () => void;
}) {
  const variables = dashboard?.variables ?? [];

  const write = (next: DashboardVariable[]) => onChange(next);

  return (
    <Drawer opened={opened && dashboard !== null} onClose={onClose} position="right" size={460}
      padding="md" title="Variables">
      <Stack gap="md">
        <Text size="xs" c="dimmed">
          A variable reaches a statement as <Code>$name</Code>, <Code>{"${name}"}</Code> or
          {" "}<Code>{"${name:csv}"}</Code> for a list. A single value is bound as a parameter, so it
          never becomes part of the statement.
        </Text>

        {variables.map((variable, index) => (
          <Stack key={index} gap={6}
            style={{ border: "1px solid var(--mantine-color-default-border)", borderRadius: 8, padding: 8 }}>
            <Group gap={6} wrap="nowrap">
              <TextInput size="xs" label="Name" value={variable.name} flex={1}
                onChange={e => write(variables.map((v, i) =>
                  i === index ? { ...v, name: e.currentTarget.value } : v))} />
              <TextInput size="xs" label="Label" value={variable.label ?? ""} flex={1}
                onChange={e => write(variables.map((v, i) =>
                  i === index ? { ...v, label: e.currentTarget.value } : v))} />
              <Select size="xs" label="Kind" w={110} allowDeselect={false}
                data={["Query", "Custom", "Constant", "Interval"]} value={variable.kind}
                onChange={value => write(variables.map((v, i) =>
                  i === index ? { ...v, kind: (value ?? "Custom") as DashboardVariable["kind"] } : v))} />
              <ActionIcon size="sm" variant="subtle" color="red" mt={18}
                aria-label={`Remove ${variable.name}`}
                onClick={() => write(variables.filter((_, i) => i !== index))}>
                <IconTrash size={14} />
              </ActionIcon>
            </Group>

            {variable.kind === "Query" ? (
              <Group gap={6} align="flex-end" wrap="nowrap">
                <Select size="xs" label="Connection" w={140} searchable
                  data={connections.map(one => ({ value: one.id, label: one.name }))}
                  value={variable.connectionId ?? null}
                  onChange={value => write(variables.map((v, i) =>
                    i === index ? { ...v, connectionId: value } : v))} />
                <Textarea size="xs" label="Its values come from" autosize minRows={1} flex={1}
                  styles={{ input: { fontFamily: "monospace" } }}
                  value={variable.sql ?? ""}
                  onChange={e => write(variables.map((v, i) =>
                    i === index ? { ...v, sql: e.currentTarget.value } : v))} />
              </Group>
            ) : (
              <TextInput size="xs" label="Values, separated by commas"
                value={(variable.values ?? []).join(", ")}
                onChange={e => write(variables.map((v, i) => i === index
                  ? { ...v, values: e.currentTarget.value.split(",").map(one => one.trim()).filter(Boolean) }
                  : v))} />
            )}

            <Group gap="md">
              <Select size="xs" label="Default" w={140} clearable
                data={variable.values ?? []} value={variable.default ?? null}
                onChange={value => write(variables.map((v, i) =>
                  i === index ? { ...v, default: value } : v))} />
              <SegmentedControl size="xs" mt={18}
                data={[{ label: "one", value: "one" }, { label: "several", value: "several" }]}
                value={variable.multi ? "several" : "one"}
                onChange={value => write(variables.map((v, i) =>
                  i === index ? { ...v, multi: value === "several" } : v))} />
              <SegmentedControl size="xs" mt={18}
                data={[{ label: "no All", value: "no" }, { label: "with All", value: "yes" }]}
                value={variable.includeAll ? "yes" : "no"}
                onChange={value => write(variables.map((v, i) =>
                  i === index ? { ...v, includeAll: value === "yes" } : v))} />
            </Group>
          </Stack>
        ))}

        <Divider />
        <Button size="compact-sm" variant="light" leftSection={<IconPlus size={13} />}
          onClick={() => write([...variables, {
            name: `variable${variables.length + 1}`, kind: "Custom", values: [], multi: false,
            includeAll: false,
          }])}>
          Add a variable
        </Button>
      </Stack>
    </Drawer>
  );
}
