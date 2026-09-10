import { useState } from "react";
import {
  ActionIcon, Button, Divider, Drawer, Group, NumberInput, Select, Stack, Switch, Tabs, Text,
  Textarea, TextInput,
} from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import type { Connection } from "../api";
import {
  WIDGET_TYPES, type ThresholdLevel, type Widget, type WidgetSource, type WidgetType,
} from "./model";

const LEVELS: ThresholdLevel[] = ["good", "warning", "serious", "critical"];

/// The roles each type asks about. A pie has no time column and a sankey has nothing else — asking
/// every widget about all nine would be a form nobody reads.
const rolesFor = (type: WidgetType): (keyof Widget["mapping"])[] => {
  switch (type) {
    case "Stat":
    case "Gauge":
      return ["values"];
    case "Sankey":
      return ["from", "to", "weight"];
    case "GeoMap":
      return ["latitude", "longitude", "values"];
    case "Heatmap":
      return ["category", "series", "values"];
    case "Line":
    case "Area":
    case "StackedArea":
    case "Sparkline":
      return ["time", "series", "values"];
    case "Text":
    case "Row":
      return [];
    default:
      return ["category", "series", "values"];
  }
};

const ROLE_LABELS: Record<string, string> = {
  time: "Time column",
  category: "Category column",
  series: "One series per value of",
  values: "Value columns",
  latitude: "Latitude",
  longitude: "Longitude",
  from: "Flows from",
  to: "Flows to",
  weight: "Weight",
};

/// Everything about one widget: what it is, where its rows come from, which column plays which
/// role, and how it looks. Three tabs, because those are three different questions — and keeping
/// them apart is what makes fifteen types one form rather than fifteen.
export function WidgetSettings({ widget, connections, columns, opened, onChange, onClose }: {
  widget: Widget | null;
  connections: Connection[];
  /// The columns the widget's last result had, so the roles are picked rather than typed.
  columns: string[];
  opened: boolean;
  onChange: (widget: Widget) => void;
  onClose: () => void;
}) {
  const [tab, setTab] = useState<string | null>("source");

  if (!widget) return null;

  const set = (patch: Partial<Widget>) => onChange({ ...widget, ...patch });
  const source = (patch: Partial<WidgetSource>) => set({ source: { ...widget.source, ...patch } });
  const options = (patch: Partial<Widget["options"]>) =>
    set({ options: { ...widget.options, ...patch } });
  const mapping = (patch: Partial<Widget["mapping"]>) =>
    set({ mapping: { ...widget.mapping, ...patch } });

  const connectionOptions = connections.map(one => ({ value: one.id, label: one.name }));
  const columnOptions = columns.map(one => ({ value: one, label: one }));
  const federated = widget.source.kind === "Federated";

  return (
    <Drawer opened={opened} onClose={onClose} position="right" size={460} padding="md"
      title={`Widget: ${widget.title}`}>
      <Stack gap="sm">
        <Group grow align="flex-start">
          <TextInput label="Title" value={widget.title} size="xs"
            onChange={e => set({ title: e.currentTarget.value })} />
          <Select label="Type" size="xs" value={widget.type} allowDeselect={false}
            data={Object.entries(
              WIDGET_TYPES.reduce<Record<string, typeof WIDGET_TYPES>>((groups, one) => {
                (groups[one.group] ??= []).push(one);
                return groups;
              }, {}),
            ).map(([group, items]) => ({
              group,
              items: items.map(one => ({ value: one.type, label: `${one.label} — ${one.hint}` })),
            }))}
            // Changing the type keeps the source: it is the same rows, drawn differently.
            onChange={value => value && set({ type: value as WidgetType })} />
        </Group>

        <TextInput label="Description" size="xs" value={widget.description ?? ""}
          description="Shown as a tooltip on the title"
          onChange={e => set({ description: e.currentTarget.value })} />

        <Tabs value={tab} onChange={setTab}>
          <Tabs.List>
            <Tabs.Tab value="source">Source</Tabs.Tab>
            <Tabs.Tab value="roles" disabled={rolesFor(widget.type).length === 0}>Columns</Tabs.Tab>
            <Tabs.Tab value="looks">Options</Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="source" pt="sm">
            <Stack gap="xs">
              {widget.type === "Text" ? (
                <Textarea label="Markdown" autosize minRows={6} size="xs"
                  description="Variables are filled in: $region, ${region}"
                  value={widget.options.markdown ?? ""}
                  onChange={e => options({ markdown: e.currentTarget.value })} />
              ) : widget.type === "Row" ? (
                <Text size="xs" c="dimmed">A section band has no rows of its own.</Text>
              ) : (
                <>
                  <Select label="Where the rows come from" size="xs" allowDeselect={false}
                    value={widget.source.kind}
                    data={[
                      { value: "Sql", label: "One connection, one statement" },
                      { value: "Path", label: "One connection, a resource path (Mongo, Redis, OData)" },
                      { value: "Federated", label: "Several connections, joined by the studio" },
                    ]}
                    onChange={value => source({ kind: (value ?? "Sql") as WidgetSource["kind"] })} />

                  {federated ? (
                    <FederatedSources widget={widget} connections={connectionOptions}
                      onChange={onChange} />
                  ) : (
                    <Select label="Connection" size="xs" searchable data={connectionOptions}
                      value={widget.source.connectionId ?? null}
                      onChange={value => source({ connectionId: value })} />
                  )}

                  <Textarea
                    label={federated ? "The statement that joins them" : "Statement"}
                    autosize minRows={4} size="xs" styles={{ input: { fontFamily: "monospace" } }}
                    description={federated
                      ? "Runs in DuckDB over the staged sources, by their aliases"
                      : "$__timeFilter(column), $__from, $__to, $__interval and $variable are filled in"}
                    value={widget.source.sql ?? ""}
                    onChange={e => source({ sql: e.currentTarget.value })} />

                  <NumberInput label="Row cap for this widget" size="xs" min={1} max={10000}
                    value={widget.options.limit ?? undefined}
                    onChange={value => options({ limit: typeof value === "number" ? value : null })} />
                </>
              )}
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value="roles" pt="sm">
            <Stack gap="xs">
              {columns.length === 0 ? (
                <Text size="xs" c="dimmed">
                  Run the widget once and its own columns show up here to pick from.
                </Text>
              ) : null}

              {rolesFor(widget.type).map(role => role === "values" ? (
                <Select key={role} label={ROLE_LABELS[role]} size="xs" searchable clearable
                  data={columnOptions} value={widget.mapping.values?.[0] ?? null}
                  onChange={value => mapping({ values: value ? [value] : [] })} />
              ) : (
                <Select key={role} label={ROLE_LABELS[role]} size="xs" searchable clearable
                  data={columnOptions} value={(widget.mapping[role] as string | null) ?? null}
                  onChange={value => mapping({ [role]: value })} />
              ))}

              <Text size="xs" c="dimmed">
                Left empty, the first column that is not a number names the rows and every numeric
                column is drawn — which is what `SELECT status, count(*)` means.
              </Text>
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value="looks" pt="sm">
            <Stack gap="xs">
              <Group grow>
                <TextInput label="Unit" size="xs" value={widget.options.unit ?? ""}
                  placeholder="percent, ms, currencyEUR"
                  onChange={e => options({ unit: e.currentTarget.value || null })} />
                <NumberInput label="Decimals" size="xs" min={0} max={8}
                  value={widget.options.decimals ?? undefined}
                  onChange={value => options({ decimals: typeof value === "number" ? value : null })} />
              </Group>

              <Group grow>
                <NumberInput label="Smallest" size="xs" value={widget.options.min ?? undefined}
                  description={widget.type === "Gauge" ? "A gauge needs both" : undefined}
                  onChange={value => options({ min: typeof value === "number" ? value : null })} />
                <NumberInput label="Largest" size="xs" value={widget.options.max ?? undefined}
                  onChange={value => options({ max: typeof value === "number" ? value : null })} />
              </Group>

              <Group>
                <Switch size="xs" label="Legend" checked={widget.options.legend !== false}
                  onChange={e => options({ legend: e.currentTarget.checked })} />
                <Switch size="xs" label="Horizontal"
                  checked={widget.options.orientation === "horizontal"}
                  onChange={e => options({ orientation: e.currentTarget.checked ? "horizontal" : null })} />
              </Group>

              <Text size="xs" c="dimmed">
                There is no second axis here, on purpose: two measures at different scales are two
                widgets.
              </Text>

              <Divider label="Thresholds" labelPosition="left" />
              <Thresholds widget={widget} onChange={onChange} />
            </Stack>
          </Tabs.Panel>
        </Tabs>
      </Stack>
    </Drawer>
  );
}

function FederatedSources({ widget, connections, onChange }: {
  widget: Widget;
  connections: { value: string; label: string }[];
  onChange: (widget: Widget) => void;
}) {
  const sources = widget.source.sources ?? [];

  const write = (next: typeof sources) =>
    onChange({ ...widget, source: { ...widget.source, sources: next } });

  return (
    <Stack gap={6}>
      {sources.map((one, index) => (
        <Group key={index} gap={4} align="flex-end" wrap="nowrap">
          <Select size="xs" label={index === 0 ? "Connection" : undefined} searchable
            data={connections} value={one.connectionId} w={130}
            onChange={value => write(sources.map((s, i) =>
              i === index ? { ...s, connectionId: value ?? "" } : s))} />
          <TextInput size="xs" label={index === 0 ? "Alias" : undefined} value={one.alias} w={90}
            onChange={e => write(sources.map((s, i) =>
              i === index ? { ...s, alias: e.currentTarget.value } : s))} />
          <Textarea size="xs" label={index === 0 ? "Its statement" : undefined} autosize minRows={1}
            flex={1} value={one.sql}
            onChange={e => write(sources.map((s, i) =>
              i === index ? { ...s, sql: e.currentTarget.value } : s))} />
          <ActionIcon size="sm" variant="subtle" color="red" aria-label={`Remove source ${index + 1}`}
            onClick={() => write(sources.filter((_, i) => i !== index))}>
            <IconTrash size={14} />
          </ActionIcon>
        </Group>
      ))}

      <Button size="compact-xs" variant="light" leftSection={<IconPlus size={12} />}
        onClick={() => write([...sources, {
          connectionId: connections[0]?.value ?? "", sql: "", alias: `s${sources.length + 1}`,
        }])}>
        Add a source
      </Button>
    </Stack>
  );
}

function Thresholds({ widget, onChange }: { widget: Widget; onChange: (widget: Widget) => void }) {
  const thresholds = widget.options.thresholds ?? [];

  const write = (next: typeof thresholds) =>
    onChange({ ...widget, options: { ...widget.options, thresholds: next } });

  return (
    <Stack gap={6}>
      {thresholds.map((one, index) => (
        <Group key={index} gap={4} wrap="nowrap">
          <NumberInput size="xs" value={one.value} w={110} aria-label={`Threshold ${index + 1}`}
            onChange={value => write(thresholds.map((t, i) =>
              i === index ? { ...t, value: typeof value === "number" ? value : 0 } : t))} />
          <Select size="xs" data={LEVELS} value={one.level} allowDeselect={false} flex={1}
            aria-label={`State ${index + 1}`}
            onChange={value => write(thresholds.map((t, i) =>
              i === index ? { ...t, level: (value ?? "warning") as ThresholdLevel } : t))} />
          <ActionIcon size="sm" variant="subtle" color="red" aria-label={`Remove threshold ${index + 1}`}
            onClick={() => write(thresholds.filter((_, i) => i !== index))}>
            <IconTrash size={14} />
          </ActionIcon>
        </Group>
      ))}

      <Button size="compact-xs" variant="light" leftSection={<IconPlus size={12} />}
        onClick={() => write([...thresholds, { value: 0, level: "warning" }])}>
        Add a threshold
      </Button>

      <Text size="xs" c="dimmed">
        The four states are reserved: they are what a threshold means, not colours to pick. A state
        is shown with its number as well.
      </Text>
    </Stack>
  );
}
