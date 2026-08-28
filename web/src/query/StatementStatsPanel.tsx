import { useEffect, useState } from "react";
import {
  Alert, Badge, Group, Loader, ScrollArea, Select, Stack, Table, Text, Tooltip, UnstyledButton,
} from "@mantine/core";
import { IconTrendingDown, IconTrendingUp } from "@tabler/icons-react";
import { historyStats, type StatementStatsDto } from "../api";

/// What this connection spends its time on, and what changed.
///
/// The history holds every run with its elapsed time, and nobody reads it as a whole: two thousand
/// statements answer no question. Grouped by fingerprint — the same query with different values is
/// one row — they answer two: where the time goes, and what got slower.
export function StatementStatsPanel({ connectionId, onOpen }: {
  connectionId?: string;
  /// Opens one of the statements in a query tab.
  onOpen?: (sql: string) => void;
}) {
  const [days, setDays] = useState("30");
  const [statements, setStatements] = useState<StatementStatsDto[] | null>(null);
  const [runs, setRuns] = useState(0);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setStatements(null);

    historyStats({ connectionId, days: Number(days) })
      .then(report => {
        if (cancelled) return;
        setStatements(report.statements);
        setRuns(report.runs);
      })
      .catch(e => { if (!cancelled) setError(e.message); });

    return () => { cancelled = true; };
  }, [connectionId, days]);

  return (
    <Stack gap={4} p={4} h="100%">
      <Group gap="xs">
        <Select size="xs" w={110} aria-label="Window" value={days}
          data={[
            { value: "1", label: "last day" },
            { value: "7", label: "last week" },
            { value: "30", label: "last month" },
            { value: "365", label: "last year" },
          ]}
          onChange={value => setDays(value ?? "30")} />
        {statements && (
          <Text size="xs" c="dimmed">
            {runs} runs · {statements.length} statements
          </Text>
        )}
      </Group>

      {error && <Alert color="yellow" variant="light">{error}</Alert>}
      {!statements && !error && <Loader size="xs" />}

      {statements?.length === 0 && (
        <Text size="xs" c="dimmed">Nothing ran in this window.</Text>
      )}

      <ScrollArea style={{ flex: 1 }}>
        <Table striped highlightOnHover fz="xs" stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Statement</Table.Th><Table.Th>Runs</Table.Th>
              <Table.Th>Average</Table.Th><Table.Th>Slowest</Table.Th><Table.Th>Trend</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {statements?.map(statement => (
              <Table.Tr key={statement.fingerprint}>
                <Table.Td style={{ maxWidth: 340 }}>
                  <UnstyledButton onClick={() => onOpen?.(statement.example)}>
                    <Text size="xs" ff="monospace" lineClamp={2}>{statement.fingerprint}</Text>
                  </UnstyledButton>
                </Table.Td>
                <Table.Td>
                  <Group gap={4}>
                    {statement.runs}
                    {statement.failures > 0 && (
                      <Badge size="xs" color="red">{statement.failures} failed</Badge>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td>{ms(statement.averageMs)}</Table.Td>
                <Table.Td>{ms(statement.slowestMs)}</Table.Td>
                <Table.Td>
                  {statement.trend === null ? (
                    <Text size="xs" c="dimmed">—</Text>
                  ) : (
                    <Tooltip label={describe(statement.trend)}>
                      <Group gap={2}>
                        {statement.trend >= 1.25 && <IconTrendingUp size={13} color="var(--mantine-color-red-6)" />}
                        {statement.trend <= 0.8 && <IconTrendingDown size={13} color="var(--mantine-color-green-6)" />}
                        <Text size="xs" c={statement.trend >= 1.25 ? "red" : undefined}>
                          {statement.trend}×
                        </Text>
                      </Group>
                    </Tooltip>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

/// The same sentence the server would write, so the tooltip and the API agree.
function describe(trend: number): string {
  if (trend >= 1.25) return `${trend.toFixed(1)}× slower than it was`;
  if (trend <= 0.8) return `${(1 / trend).toFixed(1)}× faster than it was`;
  return "about the same";
}

function ms(value: number): string {
  if (value === 0) return "—";
  return value < 1000 ? `${value} ms` : `${(value / 1000).toFixed(1)} s`;
}
