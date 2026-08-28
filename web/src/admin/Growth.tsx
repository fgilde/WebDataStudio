import { useEffect, useState } from "react";
import { Alert, Badge, Group, Loader, ScrollArea, Select, Stack, Table, Text } from "@mantine/core";
import { tableSizes, type SizesDto } from "../api";
import { formatBytes } from "../redis/format";

/// How big every table is, and how much bigger than it was.
///
/// Looking is what records it: the first look has sizes and no growth, the second has both. That
/// beats a setting somebody has to find and switch on before the studio starts remembering.
export function Growth({ connectionId }: { connectionId: string }) {
  const [days, setDays] = useState("30");
  const [data, setData] = useState<SizesDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setData(null);

    tableSizes(connectionId, Number(days))
      .then(value => { if (!cancelled) setData(value); })
      .catch(e => { if (!cancelled) setError(e.message); });

    return () => { cancelled = true; };
  }, [connectionId, days]);

  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;
  if (!data) return <Loader size="xs" m="sm" />;
  if (!data.available) return <Text size="xs" c="dimmed" p="xs">{data.reason}</Text>;

  return (
    <Stack gap={4} p="xs">
      <Group gap="xs">
        <Select size="xs" w={120} aria-label="Growth window" value={days}
          data={[
            { value: "7", label: "last week" },
            { value: "30", label: "last month" },
            { value: "90", label: "last quarter" },
            { value: "365", label: "last year" },
          ]}
          onChange={value => setDays(value ?? "30")} />
        <Text size="xs" c="dimmed">
          {data.tables.length} tables · {data.growth.length} with a history
        </Text>
      </Group>

      {data.growth.length === 0 && (
        <Text size="xs" c="dimmed">
          Sizes recorded. Growth needs a second look — come back after the next one.
        </Text>
      )}

      <ScrollArea h={300}>
        <Table striped highlightOnHover fz="xs" stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Table</Table.Th><Table.Th>Now</Table.Th>
              <Table.Th>Change</Table.Th><Table.Th>Per day</Table.Th><Table.Th>Rows</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(data.growth.length > 0 ? data.growth : []).map(entry => (
              <Table.Tr key={`${entry.schema}.${entry.table}`}>
                <Table.Td>{entry.schema ? `${entry.schema}.${entry.table}` : entry.table}</Table.Td>
                <Table.Td>{formatBytes(entry.lastBytes)}</Table.Td>
                <Table.Td>
                  <Group gap={4}>
                    <Text size="xs" c={entry.delta > 0 ? "orange" : entry.delta < 0 ? "green" : undefined}>
                      {entry.delta > 0 ? "+" : ""}{formatBytes(entry.delta)}
                    </Text>
                    {entry.percent !== null && (
                      <Badge size="xs" variant="light"
                        color={entry.percent >= 25 ? "orange" : "gray"}>
                        {entry.percent > 0 ? "+" : ""}{entry.percent}%
                      </Badge>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td>{entry.perDay === 0 ? "—" : `${formatBytes(entry.perDay)}/day`}</Table.Td>
                <Table.Td>{entry.rows ?? "—"}</Table.Td>
              </Table.Tr>
            ))}

            {/* No history yet: the sizes themselves are still worth showing. */}
            {data.growth.length === 0 && data.tables.slice(0, 30).map(table => (
              <Table.Tr key={`${table.schema}.${table.table}`}>
                <Table.Td>{table.schema ? `${table.schema}.${table.table}` : table.table}</Table.Td>
                <Table.Td>{formatBytes(table.bytes)}</Table.Td>
                <Table.Td>—</Table.Td>
                <Table.Td>—</Table.Td>
                <Table.Td>{table.rows ?? "—"}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}
