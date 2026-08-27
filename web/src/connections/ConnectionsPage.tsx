import { useEffect, useState } from "react";
import { ActionIcon, Badge, Button, Group, Modal, Stack, Table, Text, Title } from "@mantine/core";
import { IconBucket, IconKey, IconPlus, IconTrash } from "@tabler/icons-react";
import { createConnection, deleteConnection, listConnections, type Connection } from "../api";
import { ConnectionForm } from "./ConnectionForm";
import { EntraSignInModal } from "./EntraSignInModal";
import { StorageWizard } from "./StorageWizard";

export function ConnectionsPage() {
  const [items, setItems] = useState<Connection[]>([]);
  const [adding, setAdding] = useState(false);
  const [signingIn, setSigningIn] = useState<Connection | null>(null);
  const [addingBucket, setAddingBucket] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => listConnections().then(setItems).catch(e => setError(e.message));
  useEffect(() => { refresh(); }, []);

  return (
    <Stack p="md">
      <Group justify="space-between">
        <Title order={4}>Connections</Title>
        <Group gap="xs">
          {/* A bucket is a URL, which is a poor thing to type: its own form asks for the pieces. */}
          <Button variant="default" leftSection={<IconBucket size={16} />}
            onClick={() => setAddingBucket(true)}>Add a bucket</Button>
          <Button leftSection={<IconPlus size={16} />} onClick={() => setAdding(true)}>Add connection</Button>
        </Group>
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
                {c.interactive && <Badge color="blue" variant="light" ml={4}>sign-in</Badge>}
              </Table.Td>
              <Table.Td>
                {/* A connection opened as a person: nothing can be read from it until somebody has
                    signed in, so the sign-in is offered here rather than behind a failed query. */}
                {c.interactive && (
                  <ActionIcon variant="subtle" aria-label={`Sign in to ${c.name}`}
                    onClick={() => setSigningIn(c)}>
                    <IconKey size={16} />
                  </ActionIcon>
                )}
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

      {signingIn && (
        <EntraSignInModal connectionId={signingIn.id} name={signingIn.name} opened
          onClose={() => setSigningIn(null)} />
      )}

      <StorageWizard opened={addingBucket} onClose={() => setAddingBucket(false)}
        onCreated={refresh} />

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
