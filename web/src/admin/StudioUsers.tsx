import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Code, Group, Loader, Modal, PasswordInput, Select, Stack,
  Table, TagsInput, Text, TextInput, Tooltip,
} from "@mantine/core";
import { IconPencil, IconPlus, IconTrash } from "@tabler/icons-react";
import {
  createStudioUser, deleteStudioUser, hashStudioPassword, listStudioUsers, updateStudioUser,
  type AccountInput, type StudioUserDto, type StudioUsersDto,
} from "../api";

const roleColour = (role: string) =>
  role === "admin" ? "red" : role === "editor" ? "blue" : "gray";

const roles = [
  { value: "admin", label: "admin — everything, this panel included" },
  { value: "editor", label: "editor — reads and writes data" },
  { value: "viewer", label: "viewer — reads only" },
];

const said = (e: unknown) => (e instanceof Error ? e.message : String(e));

interface Editing {
  /// The account being changed, or null while making a new one.
  of: StudioUserDto | null;
  name: string;
  password: string;
  role: string;
  connections: string[];
}

const blank = (): Editing => ({ of: null, name: "", password: "", role: "editor", connections: [] });

const from = (user: StudioUserDto): Editing => ({
  of: user, name: user.name, password: "", role: user.role, connections: user.connections,
});

/// Who may sign in to this studio. Two kinds of account live here: the ones the deployment wrote
/// down in `WDS_USERS`, shown and never changed from a browser, and the ones an admin makes, which
/// this panel owns. Passwords only ever travel towards the server.
export function StudioUsers() {
  const [state, setState] = useState<StudioUsersDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [busy, setBusy] = useState(false);
  const [removing, setRemoving] = useState<StudioUserDto | null>(null);
  // Hashing here rather than in the browser: the iteration count and the format then come from the
  // same code that verifies them.
  const [password, setPassword] = useState("");
  const [hash, setHash] = useState<string | null>(null);

  const reload = () => listStudioUsers().then(setState).catch(e => setError(said(e)));

  useEffect(() => { void reload(); }, []);

  if (error && !state) return <Text c="red" size="xs" p="xs">{error}</Text>;
  if (!state) return <Loader size="xs" m="xs" />;

  const body = (e: Editing): AccountInput => ({
    name: e.name.trim(),
    // An empty field on an edit means "keep it"; on a new account the server refuses it.
    password: e.password.length > 0 ? e.password : undefined,
    role: e.role,
    connections: e.connections,
  });

  const submit = (e: Editing) => {
    setBusy(true);
    setError(null);
    const sent = e.of
      ? updateStudioUser(e.of.name, body(e))
      : createStudioUser(body(e)).then(() => undefined);

    sent.then(() => { setEditing(null); return reload(); })
      .catch(x => setError(said(x)))
      .finally(() => setBusy(false));
  };

  const remove = (user: StudioUserDto) => {
    setBusy(true);
    setError(null);
    deleteStudioUser(user.name)
      .then(() => { setRemoving(null); return reload(); })
      .catch(x => setError(said(x)))
      .finally(() => setBusy(false));
  };

  return (
    <Stack gap="xs" p="xs">
      {state.anonymous ? (
        <Alert p="xs" color="yellow">
          <Text size="sm">
            This studio has no accounts, so it needs no login and everyone who reaches it has full
            access. Make one here, or set <Code>WDS_USERS</Code>.
          </Text>
        </Alert>
      ) : null}

      <Group justify="space-between" align="flex-start" wrap="nowrap">
        <Text size="xs" c="dimmed">
          Accounts from <Code>{state.source}</Code> belong to the deployment: change those there and
          roll the container out. Accounts made here live in this studio's own store.
        </Text>
        {state.writable ? (
          <Button size="compact-sm" leftSection={<IconPlus size={14} />}
            onClick={() => setEditing(blank())}>
            Add account
          </Button>
        ) : (
          <Tooltip label={"This studio cannot write its data directory, so it keeps no accounts of "
            + "its own."} multiline w={260}>
            <Badge size="sm" variant="light" color="orange">read-only store</Badge>
          </Tooltip>
        )}
      </Group>

      {error && editing === null && removing === null
        ? <Text c="red" size="xs">{error}</Text>
        : null}

      <Group gap="xs" align="flex-end">
        <TextInput size="xs" label="Hash a password for WDS_USERS" flex={1} value={password}
          onChange={e => setPassword(e.currentTarget.value)} />
        <Button size="compact-sm" variant="default" disabled={password.length === 0}
          onClick={() => hashStudioPassword(password)
            .then(r => setHash(r.hash))
            .catch(e => setError(said(e)))}>
          Hash
        </Button>
      </Group>
      {hash ? (
        <Code block style={{ whiteSpace: "pre-wrap", wordBreak: "break-all" }}>{hash}</Code>
      ) : null}

      <Table striped withTableBorder>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Name</Table.Th>
            <Table.Th>Role</Table.Th>
            <Table.Th>Connections</Table.Th>
            <Table.Th>Password</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {state.users.length === 0 ? (
            <Table.Tr>
              <Table.Td colSpan={5}>
                <Text size="xs" c="dimmed">No accounts yet.</Text>
              </Table.Td>
            </Table.Tr>
          ) : null}
          {state.users.map(user => (
            <Table.Tr key={user.name}>
              <Table.Td>
                <Group gap={6} wrap="nowrap">
                  <Text size="sm">{user.name}</Text>
                  {user.source === "Environment" ? (
                    <Badge size="xs" variant="light" color="gray">from the environment</Badge>
                  ) : null}
                </Group>
              </Table.Td>
              <Table.Td>
                <Badge size="sm" variant="light" color={roleColour(user.role)}>{user.role}</Badge>
              </Table.Td>
              <Table.Td>
                {user.connections.length === 0
                  ? <Text size="xs" c="dimmed">all of them</Text>
                  : (
                    <Group gap={4}>
                      {user.connections.map(c => (
                        <Badge key={c} size="sm" variant="outline">{c}</Badge>
                      ))}
                    </Group>
                  )}
              </Table.Td>
              <Table.Td>
                {user.hashed
                  ? <Badge size="sm" variant="light" color="green">hashed</Badge>
                  : <Badge size="sm" variant="light" color="orange">plain text</Badge>}
              </Table.Td>
              <Table.Td>
                {user.source === "Stored" ? (
                  <Group gap={4} justify="flex-end" wrap="nowrap">
                    <ActionIcon size="sm" variant="subtle" aria-label={`Edit ${user.name}`}
                      onClick={() => setEditing(from(user))}>
                      <IconPencil size={14} />
                    </ActionIcon>
                    <ActionIcon size="sm" variant="subtle" color="red"
                      aria-label={`Delete ${user.name}`} onClick={() => setRemoving(user)}>
                      <IconTrash size={14} />
                    </ActionIcon>
                  </Group>
                ) : null}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>

      <Modal opened={editing !== null} onClose={() => setEditing(null)} withinPortal
        title={editing?.of ? `Edit ${editing.of.name}` : "Add account"}>
        {editing ? (
          <Stack gap="sm">
            <TextInput label="Name" value={editing.name} disabled={editing.of !== null}
              onChange={e => setEditing({ ...editing, name: e.currentTarget.value })} />
            <PasswordInput label="Password" value={editing.password}
              description={editing.of ? "Leave empty to keep the password they have." : undefined}
              onChange={e => setEditing({ ...editing, password: e.currentTarget.value })} />
            <Select label="Role" data={roles} value={editing.role} allowDeselect={false}
              onChange={v => setEditing({ ...editing, role: v ?? "editor" })} />
            <TagsInput label="Connections" value={editing.connections}
              description="Names of the connections they may use. Empty means all of them."
              onChange={v => setEditing({ ...editing, connections: v })} />
            {error ? <Text c="red" size="xs">{error}</Text> : null}
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setEditing(null)}>Cancel</Button>
              <Button loading={busy} onClick={() => submit(editing)}>
                {editing.of ? "Save" : "Create"}
              </Button>
            </Group>
          </Stack>
        ) : null}
      </Modal>

      <Modal opened={removing !== null} onClose={() => setRemoving(null)} withinPortal
        title={removing ? `Delete ${removing.name}?` : ""}>
        <Stack gap="sm">
          <Text size="sm">
            This account cannot sign in afterwards. Nothing else about the studio changes.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRemoving(null)}>Cancel</Button>
            <Button color="red" loading={busy}
              onClick={() => removing && remove(removing)}>Delete</Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
