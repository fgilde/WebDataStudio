import { Group, NumberInput, ScrollArea, Table, Text } from "@mantine/core";
import { useState } from "react";

/// Columns become rows. For a wide table with few rows this is the only readable layout, and it
/// is how DESCRIBE-style output reads anyway.
export function TransposedView({ columns, rows, maxRows = 12 }: {
  columns: { name: string; dataType: string }[];
  rows: unknown[][];
  maxRows?: number;
}) {
  const [offset, setOffset] = useState(0);
  const window = rows.slice(offset, offset + maxRows);

  if (rows.length === 0) return <Text size="xs" c="dimmed" p="xs">No rows.</Text>;

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4}>
        <Text size="xs" c="dimmed">
          rows {offset + 1}–{Math.min(offset + maxRows, rows.length)} of {rows.length}
        </Text>
        <NumberInput size="xs" w={110} min={1} max={rows.length} value={offset + 1}
          aria-label="First row" onChange={v => setOffset(Math.max(0, Number(v || 1) - 1))} />
      </Group>

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <Table fz="xs" striped withColumnBorders stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Column</Table.Th>
              {window.map((_, i) => <Table.Th key={i}>#{offset + i + 1}</Table.Th>)}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {columns.map((column, c) => (
              <Table.Tr key={column.name}>
                <Table.Td>
                  <Text span size="xs" fw={600}>{column.name}</Text>
                  <Text span size="10px" c="dimmed"> {column.dataType}</Text>
                </Table.Td>
                {window.map((row, i) => (
                  <Table.Td key={i} style={{ fontFamily: "monospace" }}>
                    {row[c] === null || row[c] === undefined
                      ? <Text span size="xs" c="dimmed">NULL</Text>
                      : String(row[c])}
                  </Table.Td>
                ))}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </div>
  );
}
