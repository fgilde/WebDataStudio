import { useMemo, useState } from "react";
import { Group, MultiSelect, SegmentedControl, Select, Text } from "@mantine/core";
import { inferChart, type ChartColumn } from "./inferChart";

const PALETTE = [
  "var(--mantine-color-blue-5)", "var(--mantine-color-teal-5)", "var(--mantine-color-grape-5)",
  "var(--mantine-color-orange-5)", "var(--mantine-color-lime-5)", "var(--mantine-color-cyan-5)",
];

const num = (value: unknown): number => {
  const parsed = typeof value === "number" ? value : Number(String(value ?? ""));
  return Number.isFinite(parsed) ? parsed : 0;
};

/// Inline SVG rather than a charting dependency: bar, line and pie over one label column is a
/// small amount of geometry, and it inherits the theme's colours for free.
export function ResultChart({ columns, rows }: { columns: ChartColumn[]; rows: unknown[][] }) {
  const suggestion = useMemo(() => inferChart(columns, rows), [columns, rows]);
  const [kind, setKind] = useState<string | null>(null);
  const [label, setLabel] = useState<string | null>(null);
  const [values, setValues] = useState<string[] | null>(null);

  if (!suggestion)
    return <Text size="xs" c="dimmed" p="xs">This result has nothing numeric to chart.</Text>;

  const activeKind = (kind ?? suggestion.kind) as "bar" | "line" | "pie";
  const labelIndex = Number(label ?? suggestion.labelColumn);
  const valueIndexes = (values ?? suggestion.valueColumns.map(String)).map(Number);

  // A chart of 100 000 points is unreadable and slow; the first slice tells the story.
  const capped = rows.slice(0, 200);
  const options = columns.map((c, i) => ({ value: String(i), label: c.name }));

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4}>
        <SegmentedControl size="xs" value={activeKind} onChange={setKind}
          data={[{ label: "Bar", value: "bar" }, { label: "Line", value: "line" }, { label: "Pie", value: "pie" }]} />
        <Select size="xs" w={150} label={undefined} data={options} value={String(labelIndex)}
          onChange={setLabel} aria-label="Label column" />
        <MultiSelect size="xs" w={240} data={options} value={valueIndexes.map(String)}
          onChange={setValues} aria-label="Value columns" placeholder="Values" />
        {rows.length > capped.length
          ? <Text size="xs" c="dimmed">showing the first {capped.length} of {rows.length} rows</Text>
          : null}
      </Group>

      <div style={{ flex: 1, minHeight: 0, overflow: "auto", padding: 8 }}>
        {activeKind === "pie"
          ? <Pie rows={capped} labelIndex={labelIndex} valueIndex={valueIndexes[0] ?? 0} />
          : <Cartesian rows={capped} labelIndex={labelIndex} valueIndexes={valueIndexes} kind={activeKind} />}
      </div>
    </div>
  );
}

function Cartesian({ rows, labelIndex, valueIndexes, kind }: {
  rows: unknown[][]; labelIndex: number; valueIndexes: number[]; kind: "bar" | "line";
}) {
  const width = 900;
  const height = 320;
  const padding = { left: 56, right: 12, top: 12, bottom: 42 };
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;

  const series = valueIndexes.map(index => rows.map(row => num(row[index])));
  const max = Math.max(1, ...series.flat());
  const min = Math.min(0, ...series.flat());
  const scale = (value: number) => plotHeight - ((value - min) / (max - min || 1)) * plotHeight;

  const step = plotWidth / Math.max(1, rows.length);
  const ticks = [0, 0.25, 0.5, 0.75, 1].map(t => min + (max - min) * t);

  return (
    <svg width="100%" viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`${kind} chart`}>
      <g transform={`translate(${padding.left},${padding.top})`}>
        {ticks.map(tick => (
          <g key={tick}>
            <line x1={0} x2={plotWidth} y1={scale(tick)} y2={scale(tick)}
              stroke="var(--mantine-color-default-border)" />
            <text x={-8} y={scale(tick) + 4} textAnchor="end" fontSize="10" fill="currentColor" opacity={0.6}>
              {Math.round(tick * 100) / 100}
            </text>
          </g>
        ))}

        {series.map((points, s) =>
          kind === "bar"
            ? points.map((value, i) => (
                <rect key={`${s}-${i}`}
                  x={i * step + (s * step) / (series.length + 1) + 2}
                  y={scale(value)}
                  width={Math.max(1, step / (series.length + 1) - 2)}
                  height={Math.max(0, scale(min) - scale(value))}
                  fill={PALETTE[s % PALETTE.length]} />
              ))
            : (
              <polyline key={s} fill="none" strokeWidth={2} stroke={PALETTE[s % PALETTE.length]}
                points={points.map((value, i) => `${i * step + step / 2},${scale(value)}`).join(" ")} />
            ))}

        {/* Only every nth label, otherwise they overlap into a smudge. */}
        {rows.map((row, i) => (i % Math.ceil(rows.length / 12 || 1) === 0 ? (
          <text key={i} x={i * step + step / 2} y={plotHeight + 16} fontSize="10" textAnchor="middle"
            fill="currentColor" opacity={0.7}>
            {String(row[labelIndex] ?? "").slice(0, 12)}
          </text>
        ) : null))}
      </g>
    </svg>
  );
}

function Pie({ rows, labelIndex, valueIndex }: {
  rows: unknown[][]; labelIndex: number; valueIndex: number;
}) {
  const values = rows.map(row => Math.abs(num(row[valueIndex])));
  const total = values.reduce((a, b) => a + b, 0) || 1;

  let angle = -Math.PI / 2;
  const radius = 120;
  const centre = 140;

  const slices = values.map((value, i) => {
    const sweep = (value / total) * Math.PI * 2;
    const from = angle;
    angle += sweep;

    const x1 = centre + radius * Math.cos(from);
    const y1 = centre + radius * Math.sin(from);
    const x2 = centre + radius * Math.cos(angle);
    const y2 = centre + radius * Math.sin(angle);

    return {
      // A single slice would draw an invisible zero-length arc; a full circle covers it.
      path: values.length === 1
        ? `M ${centre - radius} ${centre} a ${radius} ${radius} 0 1 0 ${radius * 2} 0 a ${radius} ${radius} 0 1 0 ${-radius * 2} 0`
        : `M ${centre} ${centre} L ${x1} ${y1} A ${radius} ${radius} 0 ${sweep > Math.PI ? 1 : 0} 1 ${x2} ${y2} Z`,
      colour: PALETTE[i % PALETTE.length],
      label: String(rows[i][labelIndex] ?? ""),
      share: Math.round((value / total) * 1000) / 10,
    };
  });

  return (
    <div style={{ display: "flex", gap: 24, alignItems: "center", flexWrap: "wrap" }}>
      <svg width={280} height={280} role="img" aria-label="pie chart">
        {slices.map((slice, i) => <path key={i} d={slice.path} fill={slice.colour} />)}
      </svg>
      <div>
        {slices.map((slice, i) => (
          <Group key={i} gap={6} mb={2}>
            <span style={{ width: 10, height: 10, background: slice.colour, borderRadius: 2 }} />
            <Text size="xs">{slice.label} · {slice.share}%</Text>
          </Group>
        ))}
      </div>
    </div>
  );
}
