import { Group, NumberInput, ScrollArea, Stack, Table, Text } from "@mantine/core";
import type { StatementResult } from "../query/resultStore";
import { CellValue } from "./CellValue";

/// Form view for rows with many columns: one row at a time, labels on the left.
export function RowFormView({ result, index, onIndexChange }: {
  result: StatementResult;
  index: number;
  onIndexChange: (index: number) => void;
}) {
  const row = result.rows[index];
  if (!row) return <Text size="xs" c="dimmed" p="xs">No row selected.</Text>;

  return (
    <Stack gap={4} p="xs" h="100%">
      <Group gap={6}>
        <Text size="xs" c="dimmed">Row</Text>
        <NumberInput size="xs" w={90} min={1} max={result.rows.length} value={index + 1}
          onChange={v => onIndexChange(Math.min(Math.max(Number(v) - 1, 0), result.rows.length - 1))} />
        <Text size="xs" c="dimmed">of {result.rows.length}</Text>
      </Group>
      <ScrollArea style={{ flex: 1 }}>
        <Table fz="xs" withRowBorders={false}>
          <Table.Tbody>
            {result.columns.map((c, i) => (
              <Table.Tr key={c.name}>
                <Table.Td w={180} style={{ verticalAlign: "top" }}>
                  <Text size="xs" fw={600}>{c.name}</Text>
                  <Text size="10px" c="dimmed">{c.dataType}</Text>
                </Table.Td>
                <Table.Td><CellValue value={row[i]} /></Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}
