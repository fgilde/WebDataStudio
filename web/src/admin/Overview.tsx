import { useCallback, useState } from "react";
import {
  Alert, Badge, Group, Paper, Progress, SegmentedControl, Stack, Table, Text,
} from "@mantine/core";
import { serverActivity, serverStats, type ActivityDto, type ServerStatsDto } from "../api";
import { sparklinePath, useMetricHistory } from "./history";
import { BlockingTree } from "./BlockingTree";
import { TimeChart } from "./TimeChart";

interface Sample { stats: ServerStatsDto; activity: ActivityDto; at: number }

/// The tab that answers "what is happening right now", which the eight tabs next to it could only
/// answer between them. Polled, with a short history per number so a tile shows a direction rather
/// than a value that jumps.
export function Overview({ connectionId }: { connectionId: string }) {
  const sample = useCallback(async (): Promise<Sample> => {
    const [stats, activity] = await Promise.all([
      serverStats(connectionId),
      serverActivity(connectionId),
    ]);

    return { stats, activity, at: Date.now() };
  }, [connectionId]);

  const { samples, latest, error } = useMetricHistory(sample, 5_000, 360);
  const [span, setSpan] = useState("60");

  if (error && latest === null)
    return <Alert color="red" m="xs"><Text size="xs">{error}</Text></Alert>;

  if (latest === null) return <Text size="xs" c="dimmed" p="sm">Reading the server…</Text>;

  const metric = (name: string) =>
    latest.stats.metrics.find(entry => entry.name.toLowerCase().includes(name.toLowerCase()));

  // The graphs show a slice of what is kept, so a wider window costs nothing but is there when the
  // question is "since when".
  const shown = samples.slice(-Number(span));

  const series = (name: string) => samples
    .map(entry => Number(entry.stats.metrics
      .find(m => m.name.toLowerCase().includes(name.toLowerCase()))?.value
      .replace(/[^0-9.]/g, "") ?? ""))
    .filter(value => Number.isFinite(value));

  const windowed = (values: number[]) => values.slice(-Number(span));

  const running = latest.activity.operations;
  const longest = running.reduce((max, operation) => Math.max(max, operation.elapsedMs), 0);

  return (
    <Stack gap="sm" p="xs" style={{ overflowY: "auto", height: "100%" }}>
      {error ? <Alert color="yellow" p={6}><Text size="xs">{error}</Text></Alert> : null}

      <Group gap="sm" wrap="wrap">
        <Tile label="Connections" value={metric("connection")?.value ?? "—"} series={series("connection")} />
        <Tile label="Cache hit" value={metric("cache")?.value ?? "—"} series={series("cache")} />
        <Tile label="Waiting" value={String(latest.activity.waits.length)}
          series={samples.map(entry => entry.activity.waits.length)}
          tone={latest.activity.waits.length > 0 ? "red" : undefined} />
        <Tile label="Running" value={String(running.length)}
          series={samples.map(entry => entry.activity.operations.length)} />
        <Tile label="Longest" value={longest > 0 ? `${Math.round(longest / 1000)}s` : "—"}
          series={samples.map(entry => Math.round(
            entry.activity.operations.reduce((max, o) => Math.max(max, o.elapsedMs), 0) / 1000))}
          tone={longest > 30_000 ? "red" : undefined} />
        <Tile label="Size" value={metric("size")?.value ?? metric("database")?.value ?? "—"}
          series={series("size")} />
      </Group>

      {/* The same numbers as the tiles, over the window rather than the last reading — pgAdmin's
          dashboard graphs, without a second polling loop to keep in step. */}
      <div>
        <Group justify="space-between" mb={4}>
          <Text size="xs" fw={600}>Over time</Text>
          <SegmentedControl size="xs" value={span} onChange={setSpan} data={[
            { label: "5 min", value: "60" },
            { label: "15 min", value: "180" },
            { label: "30 min", value: "360" },
          ]} />
        </Group>
        <Group gap="xs" align="stretch" wrap="wrap">
          <TimeChart title="Sessions" series={[
            { label: "connections", color: "var(--mantine-color-blue-5)", values: windowed(series("connection")) },
            { label: "running", color: "var(--mantine-color-teal-5)", values: shown.map(e => e.activity.operations.length) },
            { label: "waiting", color: "var(--mantine-color-red-5)", values: shown.map(e => e.activity.waits.length) },
          ]} />
          <TimeChart title="Throughput" series={[
            { label: "cache hit", color: "var(--mantine-color-grape-5)", values: windowed(series("cache")) },
            { label: "transactions", color: "var(--mantine-color-orange-5)", values: windowed(series("transaction")) },
            { label: "rows", color: "var(--mantine-color-cyan-5)", values: windowed(series("row")) },
          ]} />
        </Group>
      </div>

      {/* Who is holding up whom, as a tree: the session to deal with is the one at the root. */}
      <BlockingTree connectionId={connectionId} waits={latest.activity.waits} />

      <div>
        <Text size="xs" fw={600} mb={4}>Running now</Text>
        <Table fz="xs" striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th w={70}>Session</Table.Th><Table.Th w={110}>What</Table.Th>
              <Table.Th w={120}>Progress</Table.Th><Table.Th w={80}>For</Table.Th>
              <Table.Th>Statement</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {running.map(operation => (
              <Table.Tr key={`${operation.id}:${operation.kind}`}>
                <Table.Td>{operation.id}</Table.Td>
                <Table.Td><Badge size="xs" variant="light">{operation.kind}</Badge></Table.Td>
                <Table.Td>
                  {/* Only PostgreSQL and SQL Server report a percentage; the others say nothing
                      rather than pretending to know. */}
                  {operation.percentComplete !== null
                    ? (
                      <Group gap={4} wrap="nowrap">
                        <Progress value={operation.percentComplete} w={60} size="sm" />
                        <Text size="10px">{operation.percentComplete.toFixed(0)}%</Text>
                      </Group>
                    )
                    : <Text size="10px" c="dimmed">—</Text>}
                </Table.Td>
                <Table.Td>{operation.elapsedMs > 0 ? `${Math.round(operation.elapsedMs / 1000)}s` : "—"}</Table.Td>
                <Table.Td>
                  <Text size="10px" truncate maw={420} style={{ fontFamily: "monospace" }}>
                    {operation.statement ?? operation.target}
                  </Text>
                </Table.Td>
              </Table.Tr>
            ))}
            {running.length === 0 ? (
              <Table.Tr><Table.Td colSpan={5}>
                <Text size="xs" c="dimmed">Nothing is running.</Text>
              </Table.Td></Table.Tr>
            ) : null}
          </Table.Tbody>
        </Table>
      </div>
    </Stack>
  );
}

function Tile({ label, value, series, tone }: {
  label: string;
  value: string;
  series: number[];
  tone?: "red";
}) {
  return (
    <Paper withBorder p={8} radius="md" style={{ minWidth: 150 }}>
      <Text size="10px" c="dimmed" tt="uppercase">{label}</Text>
      <Text size="lg" fw={700} c={tone}>{value}</Text>

      {series.length > 1 ? (
        <svg width={130} height={22} style={{ display: "block" }}>
          <path d={sparklinePath(series, 130, 20)} fill="none" strokeWidth={1.5}
            stroke={tone === "red"
              ? "var(--mantine-color-red-6)"
              : "var(--mantine-primary-color-filled)"} />
        </svg>
      ) : <div style={{ height: 22 }} />}
    </Paper>
  );
}
