import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { ActionIcon, Badge, Button, Group, Modal, Stack, Table, Text, Title } from "@mantine/core";
import { IconBucket, IconEraser, IconKey, IconPlus, IconTrash } from "@tabler/icons-react";
import {
  createConnection, deleteConnection, forgetSession, listConnections, type Connection,
} from "../api";
import { BringYourOwn } from "./BringYourOwn";
import { ConnectionForm } from "./ConnectionForm";
import { EntraSignInModal } from "./EntraSignInModal";
import { StorageWizard } from "./StorageWizard";
import { useAccess } from "./useAccess";

export function ConnectionsPage() {
  const [items, setItems] = useState<Connection[]>([]);
  const [adding, setAdding] = useState(false);
  const [signingIn, setSigningIn] = useState<Connection | null>(null);
  const [addingBucket, setAddingBucket] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const access = useAccess();

  const [query, setQuery] = useSearchParams();

  const refresh = () => listConnections().then(setItems).catch(e => setError(e.message));
  useEffect(() => { refresh(); }, []);

  // "Add connection" and "Add a bucket" in the palette navigate here and say which form to open.
  // Without this the command opened the page and left the person looking for the button.
  useEffect(() => {
    if (query.get("add") === "1") setAdding(true);
    if (query.get("bucket") === "1") setAddingBucket(true);

    if (query.has("add") || query.has("bucket")) {
      const next = new URLSearchParams(query);
      next.delete("add");
      next.delete("bucket");
      // Cleared so a reload does not reopen a dialog somebody closed.
      setQuery(next, { replace: true });
    }
  }, [query, setQuery]);

  return (
    <Stack p="md">
      <Group justify="space-between">
        <Title order={4}>Connections</Title>
        <Group gap="xs">
          {/* Everything this browser brought, gone now. A studio a stranger walks up to should
              have a way out that does not involve trusting a lifetime. */}
          {access.scope === "Session" && items.length > 0 && (
            <Button variant="subtle" color="red" leftSection={<IconEraser size={16} />}
              onClick={() => forgetSession().then(refresh).catch(e => setError(e.message))}>
              Forget my connections
            </Button>
          )}
          {/* A button for a door the deployment closed would only answer with a refusal. */}
          {access.mayAdd && (
            <>
              {/* A bucket is a URL, which is a poor thing to type: its own form asks for the pieces. */}
              <Button variant="default" leftSection={<IconBucket size={16} />}
                onClick={() => setAddingBucket(true)}>Add a bucket</Button>
              <Button leftSection={<IconPlus size={16} />}
                onClick={() => setAdding(true)}>Add connection</Button>
            </>
          )}
        </Group>
      </Group>

      {/* On a studio somebody brings their own data to, this is the whole instruction they get. */}
      {items.length === 0 && (
        <BringYourOwn access={access} onAdd={() => setAdding(true)} />
      )}
      {error && <Text c="red" size="sm">{error}</Text>}
      {items.length > 0 && (
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
                {c.source === "Session" && <Badge color="grape" variant="light">from a link</Badge>}
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
                {/* Both are this studio's to drop: a stored one for everybody, a session one for
                    the browser that made it. Only the environment's are somebody else's. */}
                {c.source !== "Environment" && (
                  <ActionIcon variant="subtle" color="red" aria-label={`Delete ${c.name}`}
                    onClick={() => deleteConnection(c.id).then(refresh).catch(e => setError(e.message))}>
                    <IconTrash size={16} />
                  </ActionIcon>
                )}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      )}

      {signingIn && (
        <EntraSignInModal connectionId={signingIn.id} name={signingIn.name} opened
          onClose={() => setSigningIn(null)} />
      )}

      <StorageWizard opened={addingBucket} onClose={() => setAddingBucket(false)}
        onCreated={refresh} />

      <Modal opened={adding} onClose={() => setAdding(false)} title="Add connection">
        <ConnectionForm
          onCancel={() => setAdding(false)}
          onCreated={() => { setAdding(false); refresh(); }}
          onSubmit={async value => {
            // The answer is the connection: shown at once, not after a second round trip.
            try {
              const created = await createConnection(value);
              setItems(list => [...list.filter(c => c.id !== created.id), created]);
              setAdding(false);
              refresh();
            }
            catch (e) { setError(e instanceof Error ? e.message : String(e)); }
          }} />
      </Modal>
    </Stack>
  );
}
