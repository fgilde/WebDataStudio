import { useEffect, useState } from "react";
import { ActionIcon, Badge, Button, Group, Modal, Stack, Table, Text, Title } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { createConnection, deleteConnection, listConnections, type Connection } from "../api";
import { ConnectionForm } from "./ConnectionForm";

export function ConnectionsPage() {
  const [items, setItems] = useState<Connection[]>([]);
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => listConnections().then(setItems).catch(e => setError(e.message));
  useEffect(() => { refresh(); }, []);

  return (
    <Stack p="md">
      <Group justify="space-between">
        <Title order={4}>Connections</Title>
        <Button leftSection={<IconPlus size={16} />} onClick={() => setAdding(true)}>Add connection</Button>
      </Group>
      {error && <Text c="red" size="sm">{error}</Text>}
      <Table striped highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Name</Table.Th><Table.Th>Engine</Table.Th><Table.Th>Target</Table.Th>
            <Table.Th>Origin</Table.Th><Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {items.map(c => (
            <Table.Tr key={c.id}>
              <Table.Td>{c.name}</Table.Td>
              <Table.Td>{c.engine}</Table.Td>
              <Table.Td>{c.summary}</Table.Td>
              <Table.Td>
                {c.source === "Environment" && <Badge variant="light">from environment</Badge>}
                {c.readOnly && <Badge color="orange" variant="light" ml={4}>read-only</Badge>}
              </Table.Td>
              <Table.Td>
                {c.source === "Stored" && (
                  <ActionIcon variant="subtle" color="red"
                    onClick={() => deleteConnection(c.id).then(refresh).catch(e => setError(e.message))}>
                    <IconTrash size={16} />
                  </ActionIcon>
                )}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>

      <Modal opened={adding} onClose={() => setAdding(false)} title="Add connection">
        <ConnectionForm
          onCancel={() => setAdding(false)}
          onSubmit={async value => {
            try { await createConnection(value); setAdding(false); refresh(); }
            catch (e) { setError(e instanceof Error ? e.message : String(e)); }
          }} />
      </Modal>
    </Stack>
  );
}
