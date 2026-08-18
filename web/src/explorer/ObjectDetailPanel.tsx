import { useEffect, useState } from "react";
import { Badge, Group, Loader, ScrollArea, Table, Tabs, Text } from "@mantine/core";
import { describeObject, type ObjectDetailDto } from "../api";
import type { ExplorerSelection } from "./ExplorerTree";

const DESCRIBABLE = ["Table", "View", "MaterializedView"];

export function ObjectDetailPanel({ selection }: { selection: ExplorerSelection | null }) {
  const [detail, setDetail] = useState<ObjectDetailDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setDetail(null);
    setError(null);
    if (!selection || !DESCRIBABLE.includes(selection.node.kind)) return;

    let cancelled = false;
    describeObject(selection.connectionId, selection.node.ref)
      .then(d => { if (!cancelled) setDetail(d); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [selection]);

  if (!selection) return <Text size="xs" c="dimmed" p="xs">Select an object.</Text>;
  if (!DESCRIBABLE.includes(selection.node.kind))
    return <Text size="xs" c="dimmed" p="xs">{selection.node.label}</Text>;
  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!detail) return <Loader size="xs" m="xs" />;

  return (
    <Tabs defaultValue="columns" h="100%">
      <Tabs.List>
        <Tabs.Tab value="columns">Columns</Tabs.Tab>
        <Tabs.Tab value="indexes">Indexes</Tabs.Tab>
        <Tabs.Tab value="keys">Keys</Tabs.Tab>
        <Tabs.Tab value="info">Info</Tabs.Tab>
      </Tabs.List>

      <Tabs.Panel value="columns">
        <ScrollArea h="calc(100vh - 160px)">
          <Table striped stickyHeader fz="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th><Table.Th>Type</Table.Th>
                <Table.Th>Null</Table.Th><Table.Th>Default</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {detail.columns.map(c => (
                <Table.Tr key={c.name}>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap">
                      {c.name}
                      {c.isPrimaryKey && <Badge size="xs" variant="light">PK</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td>{c.dataType}</Table.Td>
                  <Table.Td>{c.nullable ? "yes" : "no"}</Table.Td>
                  <Table.Td>{c.default ?? ""}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </ScrollArea>
      </Tabs.Panel>

      <Tabs.Panel value="indexes">
        <Table fz="xs">
          <Table.Tbody>
            {detail.indexes.map(i => (
              <Table.Tr key={i.name}>
                <Table.Td>{i.name}</Table.Td>
                <Table.Td>{i.columns.join(", ")}</Table.Td>
                <Table.Td>{i.unique ? "unique" : ""}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Tabs.Panel>

      <Tabs.Panel value="keys">
        <Table fz="xs">
          <Table.Tbody>
            {detail.foreignKeys.map(f => (
              <Table.Tr key={f.name}>
                <Table.Td>{f.columns.join(", ")}</Table.Td>
                <Table.Td>&rarr; {f.referencedTable}({f.referencedColumns.join(", ")})</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Tabs.Panel>

      <Tabs.Panel value="info">
        <Text size="xs" p="xs">
          Rows: {detail.rowCount ?? "unknown"}<br />
          Size: {detail.sizeBytes ? `${Math.round(detail.sizeBytes / 1024)} KiB` : "unknown"}<br />
          {detail.comment}
        </Text>
      </Tabs.Panel>
    </Tabs>
  );
}
