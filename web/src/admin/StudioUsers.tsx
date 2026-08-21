import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Code, Group, Loader, Stack, Table, Text, TextInput,
} from "@mantine/core";
import { hashStudioPassword, listStudioUsers, type StudioUsersDto } from "../api";

const roleColour = (role: string) =>
  role === "admin" ? "red" : role === "editor" ? "blue" : "gray";

/// Who may sign in to this studio. Read-only on purpose: accounts come from the environment, so a
/// container rollout is the only way to change them and nobody can promote themselves here.
export function StudioUsers() {
  const [state, setState] = useState<StudioUsersDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Hashing here rather than in the browser: the iteration count and the format then come from the
  // same code that verifies them.
  const [password, setPassword] = useState("");
  const [hash, setHash] = useState<string | null>(null);

  useEffect(() => {
    listStudioUsers().then(setState)
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  if (error) return <Text c="red" size="xs" p="xs">{error}</Text>;
  if (!state) return <Loader size="xs" m="xs" />;

  if (state.anonymous)
    return (
      <Alert m="xs" p="xs" color="yellow">
        <Text size="sm">
          This studio has no accounts, so it needs no login and everyone who reaches it has full
          access. Set <Code>WDS_USERS</Code> to change that.
        </Text>
      </Alert>
    );

  return (
    <Stack gap="xs" p="xs">
      <Text size="xs" c="dimmed">
        From <Code>{state.source}</Code>. Accounts are deployment configuration: change them there
        and roll the container out.
      </Text>

      <Group gap="xs" align="flex-end">
        <TextInput size="xs" label="Hash a password for WDS_USERS" flex={1} value={password}
          onChange={e => setPassword(e.currentTarget.value)} />
        <Button size="compact-sm" variant="default" disabled={password.length === 0}
          onClick={() => hashStudioPassword(password)
            .then(r => setHash(r.hash))
            .catch(e => setError(e instanceof Error ? e.message : String(e)))}>
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
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {state.users.map(user => (
            <Table.Tr key={user.name}>
              <Table.Td>{user.name}</Table.Td>
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
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}
