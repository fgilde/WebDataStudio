import { useEffect, useState } from "react";
import { Alert, Badge, Code, Group, Loader, Stack, Table, Text } from "@mantine/core";
import { listStudioUsers, type StudioUsersDto } from "../api";

const roleColour = (role: string) =>
  role === "admin" ? "red" : role === "editor" ? "blue" : "gray";

/// Who may sign in to this studio. Read-only on purpose: accounts come from the environment, so a
/// container rollout is the only way to change them and nobody can promote themselves here.
export function StudioUsers() {
  const [state, setState] = useState<StudioUsersDto | null>(null);
  const [error, setError] = useState<string | null>(null);

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
