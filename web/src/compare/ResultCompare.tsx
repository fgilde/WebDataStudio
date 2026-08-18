import { useMemo, useState } from "react";
import { Alert, Badge, Group, MultiSelect, ScrollArea, Select, Table, Text } from "@mantine/core";
import { diffResults, type ResultSet } from "./diffResults";

export interface NamedResult { id: string; label: string; result: ResultSet }

/// Compares two results the user already has open. No round trip: both sides are in the browser
/// already, and re-running them would compare two different points in time.
export function ResultCompare({ results, initialLeft }: {
  results: NamedResult[];
  initialLeft?: string;
}) {
  const [left, setLeft] = useState<string | null>(initialLeft ?? results[0]?.id ?? null);
  const [right, setRight] = useState<string | null>(results[1]?.id ?? null);
  const [keys, setKeys] = useState<string[]>([]);

  const a = results.find(r => r.id === left);
  const b = results.find(r => r.id === right);

  const shared = useMemo(() => {
    if (!a || !b) return [];
    return a.result.columns.filter(c => b.result.columns.includes(c));
  }, [a, b]);

  const diff = useMemo(() => {
    if (!a || !b) return null;
    const chosen = keys.length > 0 ? keys : shared.slice(0, 1);
    if (chosen.length === 0) return null;

    try {
      return { value: diffResults(a.result, b.result, chosen), error: null as string | null };
    } catch (e) {
      return { value: null, error: e instanceof Error ? e.message : String(e) };
    }
  }, [a, b, keys, shared]);

  const options = results.map(r => ({ value: r.id, label: r.label }));

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4} align="flex-end">
        <Select size="xs" w={180} label="Left" data={options} value={left} onChange={setLeft} />
        <Select size="xs" w={180} label="Right" data={options} value={right} onChange={setRight} />
        <MultiSelect size="xs" w={240} label="Key columns" data={shared} value={keys} onChange={setKeys}
          placeholder={shared[0] ? `default: ${shared[0]}` : "no shared column"} />
      </Group>

      {!a || !b
        ? <Text size="xs" c="dimmed" p="xs">Pick two results to compare.</Text>
        : diff?.error
          ? <Alert color="red" variant="light" m={4}>{diff.error}</Alert>
          : diff?.value
            ? (
              <>
                <Group gap={6} px={4}>
                  <Badge size="sm" color="green">{diff.value.onlyInA.length} only left</Badge>
                  <Badge size="sm" color="yellow">{diff.value.different.length} different</Badge>
                  <Badge size="sm" color="red">{diff.value.onlyInB.length} only right</Badge>
                  <Badge size="sm" color="gray">{diff.value.identical} identical</Badge>
                </Group>

                <ScrollArea style={{ flex: 1, minHeight: 0 }} p={4}>
                  <Table fz="xs" striped stickyHeader>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>Key</Table.Th>
                        <Table.Th>Where</Table.Th>
                        <Table.Th>Columns</Table.Th>
                        <Table.Th>Left</Table.Th>
                        <Table.Th>Right</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {diff.value.different.map((row, i) => (
                        <Table.Tr key={`d${i}`}>
                          <Table.Td>{row.key.map(String).join(", ")}</Table.Td>
                          <Table.Td><Badge size="xs" color="yellow">changed</Badge></Table.Td>
                          <Table.Td>{row.changedColumns.join(", ")}</Table.Td>
                          <Table.Td>{row.left.map(String).join(" | ")}</Table.Td>
                          <Table.Td>{row.right.map(String).join(" | ")}</Table.Td>
                        </Table.Tr>
                      ))}
                      {diff.value.onlyInA.map((row, i) => (
                        <Table.Tr key={`a${i}`}>
                          <Table.Td />
                          <Table.Td><Badge size="xs" color="green">only left</Badge></Table.Td>
                          <Table.Td />
                          <Table.Td>{row.map(String).join(" | ")}</Table.Td>
                          <Table.Td />
                        </Table.Tr>
                      ))}
                      {diff.value.onlyInB.map((row, i) => (
                        <Table.Tr key={`b${i}`}>
                          <Table.Td />
                          <Table.Td><Badge size="xs" color="red">only right</Badge></Table.Td>
                          <Table.Td />
                          <Table.Td />
                          <Table.Td>{row.map(String).join(" | ")}</Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </ScrollArea>
              </>
            )
            : <Text size="xs" c="dimmed" p="xs">These two results share no column to match rows by.</Text>}
    </div>
  );
}
