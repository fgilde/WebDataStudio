import { Group, Paper, Text } from "@mantine/core";
import { sparklinePath } from "./history";

export interface ChartSeries { label: string; color: string; values: number[] }

/// Several readings over the retained window, on one set of axes. Each line is normalised to its
/// own range: the point is the shape over time, and connections and cache hit share no unit.
export function TimeChart({ title, series, height = 90 }: {
  title: string;
  series: ChartSeries[];
  height?: number;
}) {
  const width = 600;
  const drawn = series.filter(entry => entry.values.length > 0);

  return (
    <Paper withBorder p="xs" style={{ flex: "1 1 320px", minWidth: 280 }}>
      <Group justify="space-between" gap="xs" mb={4}>
        <Text size="xs" fw={600}>{title}</Text>
        <Group gap="xs">
          {drawn.map(entry => (
            <Group key={entry.label} gap={4} wrap="nowrap">
              <span style={{
                width: 8, height: 8, borderRadius: 2, background: entry.color, display: "inline-block",
              }} />
              <Text size="10px" c="dimmed">
                {entry.label} {entry.values[entry.values.length - 1]}
              </Text>
            </Group>
          ))}
        </Group>
      </Group>

      {drawn.length === 0
        ? <Text size="10px" c="dimmed">Nothing measured yet.</Text>
        : (
          <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height}
            preserveAspectRatio="none" role="img" aria-label={title}>
            {/* Three lines of grid, so a slope can be read against something. */}
            {[0.25, 0.5, 0.75].map(fraction => (
              <line key={fraction} x1={0} x2={width} y1={height * fraction} y2={height * fraction}
                stroke="currentColor" strokeOpacity={0.12} strokeWidth={1} />
            ))}
            {drawn.map(entry => (
              <path key={entry.label} d={sparklinePath(entry.values, width, height - 4)}
                transform="translate(0,2)" fill="none" stroke={entry.color} strokeWidth={1.5}
                vectorEffect="non-scaling-stroke" />
            ))}
          </svg>
        )}
    </Paper>
  );
}
