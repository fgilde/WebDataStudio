import { useMemo } from "react";
import { Group, Progress, ScrollArea, Stack, Table, Text } from "@mantine/core";
import { GeoView } from "../../geo/GeoView";
import { buildOption, format, isRefusal, roles } from "../charts/option";
import { EChart } from "../charts/EChart";
import { Markdown } from "./Markdown";
import { isChart, type Dashboard, type Widget } from "../model";
import { ink, thresholdColour } from "../palette";
import type { WidgetData } from "../useWidgetData";

const num = (value: unknown): number | null => {
  if (value === null || value === undefined || value === "") return null;
  const parsed = typeof value === "number" ? value : Number(String(value));
  return Number.isFinite(parsed) ? parsed : null;
};

const cell = (value: unknown) =>
  value === null || value === undefined
    ? <Text span size="xs" c="dimmed">null</Text>
    : String(value);

/// One headline number, and its state where thresholds say there is one.
function Stat({ widget, data, dark }: { widget: Widget; data: WidgetData; dark: boolean }) {
  const { values } = roles(data, widget.mapping);
  const value = data.rows.length > 0 && values.length > 0 ? num(data.rows[0][values[0]]) : null;
  const theme = ink(dark);
  const colour = thresholdColour(value, widget.options.thresholds, theme.text);

  // The state ships with its number, never as a colour on its own: whoever cannot see the colour
  // still reads the word.
  const state = value !== null && widget.options.thresholds?.length
    ? [...widget.options.thresholds].sort((a, b) => a.value - b.value)
      .filter(one => value >= one.value).pop()?.level
    : undefined;

  return (
    <Stack gap={2} justify="center" align="center" h="100%">
      <Text fw={700} style={{ fontSize: "clamp(20px, 6vw, 44px)", lineHeight: 1.1, color: colour }}>
        {format(value, widget.options)}
      </Text>
      {state ? <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{state}</Text> : null}
    </Stack>
  );
}

/// A label and a value per row, which is what a top ten is. The bar behind each row is the length
/// of the value, so the order is readable without reading the numbers.
function Rank({ widget, data, dark }: { widget: Widget; data: WidgetData; dark: boolean }) {
  const { category, values } = roles(data, widget.mapping);
  const theme = ink(dark);

  const rows = data.rows.slice(0, widget.options.limit ?? 50).map((row, index) => ({
    label: category < 0 ? String(index + 1) : String(row[category] ?? "∅"),
    value: values.length > 0 ? num(row[values[0]]) : null,
  }));

  const largest = Math.max(1, ...rows.map(one => Math.abs(one.value ?? 0)));

  return (
    <ScrollArea h="100%" type="auto">
      <Stack gap={6} p={2}>
        {rows.map((one, index) => (
          <div key={`${one.label}-${index}`}>
            <Group justify="space-between" gap="xs" wrap="nowrap">
              <Text size="xs" truncate>{one.label}</Text>
              <Text size="xs" fw={600}>{format(one.value, widget.options)}</Text>
            </Group>
            <Progress value={(Math.abs(one.value ?? 0) / largest) * 100} size={4} mt={2}
              color={theme.series[0]} />
          </div>
        ))}
        {rows.length === 0 ? <Text size="xs" c="dimmed">No rows.</Text> : null}
      </Stack>
    </ScrollArea>
  );
}

/// The rows as rows. Also the fallback every chart widget can switch to, which is both the
/// accessibility relief and the way to see what a picture is actually made of.
export function Rows({ data, limit }: { data: WidgetData; limit?: number | null }) {
  const shown = data.rows.slice(0, limit ?? 500);

  return (
    <ScrollArea h="100%" type="auto">
      <Table striped withTableBorder={false} verticalSpacing={2} horizontalSpacing={6} fz="xs">
        <Table.Thead>
          <Table.Tr>
            {data.columns.map(column => (
              <Table.Th key={column.name} style={{ whiteSpace: "nowrap" }}>{column.name}</Table.Th>
            ))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {shown.map((row, index) => (
            <Table.Tr key={index}>
              {row.map((value, column) => (
                <Table.Td key={column} style={{ whiteSpace: "nowrap" }}>{cell(value)}</Table.Td>
              ))}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {data.rows.length > shown.length ? (
        <Text size="xs" c="dimmed" p={4}>
          the first {shown.length} of {data.rows.length} rows
        </Text>
      ) : null}
    </ScrollArea>
  );
}

/// What a widget draws, once its rows are in. Everything about loading, errors and the menu is the
/// frame's job — this is only the picture.
export function WidgetBody({ widget, data, dashboard, chosen, dark, asRows }: {
  widget: Widget;
  data: WidgetData;
  dashboard: Dashboard;
  chosen: Record<string, string[]>;
  dark: boolean;
  asRows: boolean;
}) {
  const option = useMemo(
    () => (isChart(widget.type) && !asRows ? buildOption(widget, data, dark) : null),
    [widget, data, dark, asRows],
  );

  if (widget.type === "Text") {
    // The variables are filled in, so a text widget can say which region it is about.
    const filled = (widget.options.markdown ?? "").replace(
      /\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?/g,
      (whole, name: string) => (chosen[name] ? chosen[name].join(", ") : whole),
    );

    return (
      <ScrollArea h="100%" type="auto">
        <Stack gap={6} p={4}>
          <Markdown text={filled} />
        </Stack>
      </ScrollArea>
    );
  }

  if (asRows || widget.type === "Table") return <Rows data={data} limit={widget.options.limit} />;
  if (widget.type === "List") return <Rank widget={widget} data={data} dark={dark} />;
  if (widget.type === "Stat") return <Stat widget={widget} data={data} dark={dark} />;

  if (widget.type === "GeoMap")
    // The studio's own map: shapes to scale, no basemap, and no tile server reached out to.
    return (
      <ScrollArea h="100%" type="auto">
        <GeoView columns={data.columns.map(one => ({ name: one.name, dataType: one.dataType ?? undefined }))}
          rows={data.rows} />
      </ScrollArea>
    );

  if (option && isRefusal(option))
    return (
      <Stack justify="center" h="100%" p="xs">
        <Text size="xs" c="dimmed" ta="center">{option.refusal}</Text>
      </Stack>
    );

  if (option) return <EChart option={option.option} />;

  // A section band draws nothing itself; the canvas lays the widgets under it out.
  return dashboard.widgets.length > 0 ? null : null;
}
