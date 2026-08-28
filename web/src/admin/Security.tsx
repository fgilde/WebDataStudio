import {
  ActionIcon, Alert, Badge, Button, Group, Loader, Menu, Modal, ScrollArea, Stack, Switch,
  Table, Text, TextInput, Tooltip,
} from "@mantine/core";
import {
  IconDots, IconKey, IconPlus, IconRefresh, IconShieldCheck, IconTrash, IconUserPlus,
} from "@tabler/icons-react";
import { useEffect, useState } from "react";
import {
  applyUserChange, listUsers, previewUserChange, userGrants,
  type DbPrincipalDto, type PrivilegeGrantDto, type SecurityAction,
} from "../api";
import { ScriptConfirm, type PendingScript } from "../ddl/ScriptConfirm";
import { notifications } from "@mantine/notifications";

/// What a form is being asked for: a new account, a new role, a password, a membership, a right.
type Ask =
  | { kind: "create"; role: boolean }
  | { kind: "password"; user: string }
  | { kind: "membership"; user: string; grant: boolean }
  | { kind: "privilege"; user: string; grant: boolean };

const TITLES: Record<Ask["kind"], string> = {
  create: "New",
  password: "New password",
  membership: "Role",
  privilege: "Privilege",
};

/// The server's own accounts and roles.
///
/// Reading is best effort: listing accounts is itself a privilege, and a connection without it gets
/// an empty list rather than an error nobody can act on. Writing is the same handshake as every
/// other change in the studio — the statement is shown, and only a click runs it.
export function Security({ connectionId }: { connectionId: string }) {
  const [principals, setPrincipals] = useState<DbPrincipalDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [grants, setGrants] = useState<PrivilegeGrantDto[] | null>(null);
  const [pending, setPending] = useState<PendingScript | null>(null);
  const [ask, setAsk] = useState<Ask | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    setPrincipals(null);
    setError(null);
    listUsers(connectionId).then(setPrincipals).catch(e => setError(e.message));
  }, [connectionId, nonce]);

  useEffect(() => {
    setGrants(null);
    if (selected) userGrants(connectionId, selected).then(setGrants).catch(() => setGrants([]));
  }, [connectionId, selected, nonce]);

  /// Ask the server for the statement, then show it. Nothing has run when this returns.
  const preview = async (title: string, body: Parameters<typeof previewUserChange>[1]) => {
    try {
      const built = await previewUserChange(connectionId, body);

      setPending({
        connectionId, title, ...built,
        // Accounts are cached under a key of their own, so the apply is theirs too.
        apply: (conn: string, hash: string) => applyUserChange(conn, hash),
      });
    } catch (e) {
      notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) });
    }
  };

  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;
  if (!principals) return <Loader size="xs" m="sm" />;

  const one = principals.find(p => p.name === selected) ?? null;

  return (
    <Stack gap="xs" p="xs">
      <Group gap={6}>
        <Button size="compact-xs" leftSection={<IconUserPlus size={13} />}
          onClick={() => setAsk({ kind: "create", role: false })}>
          New account…
        </Button>
        <Button size="compact-xs" variant="default" leftSection={<IconShieldCheck size={13} />}
          onClick={() => setAsk({ kind: "create", role: true })}>
          New role…
        </Button>
        <ActionIcon size="sm" variant="subtle" aria-label="Reload accounts"
          onClick={() => setNonce(n => n + 1)}>
          <IconRefresh size={14} />
        </ActionIcon>

        <Text size="xs" c="dimmed" ml="auto">
          {principals.filter(p => p.canLogin).length} accounts ·{" "}
          {principals.filter(p => p.isRole).length} roles
        </Text>
      </Group>

      {principals.length === 0 && (
        <Alert color="gray" variant="light" p={8}>
          <Text size="xs">
            Nothing to show. Listing accounts is itself a privilege — this connection does not have it.
          </Text>
        </Alert>
      )}

      <ScrollArea h={240}>
        <Table striped highlightOnHover fz="xs" stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Name</Table.Th>
              <Table.Th>What it is</Table.Th>
              <Table.Th>Member of</Table.Th>
              <Table.Th>Until</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {principals.map(principal => (
              <Table.Tr key={principal.name} onClick={() => setSelected(principal.name)}
                style={{ cursor: "pointer" }}
                bg={principal.name === selected ? "var(--mantine-color-default-hover)" : undefined}>
                <Table.Td>{principal.name}</Table.Td>
                <Table.Td>
                  <Group gap={4}>
                    <Badge size="xs" variant="light" color={principal.isRole ? "grape" : "blue"}>
                      {principal.isRole ? "role" : "account"}
                    </Badge>
                    {principal.superuser && <Badge size="xs" color="red" variant="light">superuser</Badge>}
                    {!principal.isRole && !principal.canLogin && (
                      <Badge size="xs" color="gray" variant="light">cannot sign in</Badge>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td>{principal.memberOf.join(", ")}</Table.Td>
                <Table.Td>{principal.validUntil ?? ""}</Table.Td>
                <Table.Td>
                  <Menu position="bottom-end" withinPortal>
                    <Menu.Target>
                      <ActionIcon size="sm" variant="subtle" aria-label={`Change ${principal.name}`}
                        onClick={event => event.stopPropagation()}>
                        <IconDots size={14} />
                      </ActionIcon>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item leftSection={<IconKey size={13} />}
                        onClick={() => setAsk({ kind: "password", user: principal.name })}>
                        New password…
                      </Menu.Item>
                      <Menu.Item
                        onClick={() => preview(
                          `${principal.canLogin ? "Stop" : "Let"} ${principal.name} ${principal.canLogin ? "signing in" : "sign in"}`,
                          { user: principal.name, action: "login", canLogin: !principal.canLogin })}>
                        {principal.canLogin ? "Stop it signing in…" : "Let it sign in…"}
                      </Menu.Item>
                      <Menu.Divider />
                      <Menu.Item leftSection={<IconPlus size={13} />}
                        onClick={() => setAsk({ kind: "membership", user: principal.name, grant: true })}>
                        Put in a role…
                      </Menu.Item>
                      <Menu.Item
                        onClick={() => setAsk({ kind: "membership", user: principal.name, grant: false })}>
                        Take out of a role…
                      </Menu.Item>
                      <Menu.Item
                        onClick={() => setAsk({ kind: "privilege", user: principal.name, grant: true })}>
                        Grant a privilege…
                      </Menu.Item>
                      <Menu.Item
                        onClick={() => setAsk({ kind: "privilege", user: principal.name, grant: false })}>
                        Take a privilege back…
                      </Menu.Item>
                      <Menu.Divider />
                      <Menu.Item color="red" leftSection={<IconTrash size={13} />}
                        onClick={() => preview(`Drop ${principal.name}`,
                          { user: principal.name, action: "drop", role: principal.isRole })}>
                        Drop…
                      </Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>

      {one && (
        <Stack gap={4}>
          <Group gap={6}>
            <Text size="xs" fw={600}>{one.name}</Text>
            <Tooltip label="Rights granted to this one directly. Anything else comes from its roles.">
              <Text size="10px" c="dimmed">what it may do</Text>
            </Tooltip>
          </Group>

          {grants === null ? <Loader size="xs" /> : grants.length === 0 ? (
            <Text size="xs" c="dimmed">
              Nothing granted directly{one.memberOf.length > 0
                ? ` — what it can do comes from ${one.memberOf.join(", ")}`
                : ""}.
            </Text>
          ) : (
            <ScrollArea h={140}>
              <Table fz="xs">
                <Table.Tbody>
                  {grants.map((grant, index) => (
                    <Table.Tr key={`${grant.object}-${grant.privilege}-${index}`}>
                      <Table.Td>{grant.object}</Table.Td>
                      <Table.Td>{grant.privilege}</Table.Td>
                      <Table.Td>{grant.grantable ? "may pass it on" : ""}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea>
          )}
        </Stack>
      )}

      <AskDialog ask={ask} principals={principals} onClose={() => setAsk(null)}
        onAsk={(title, body) => { setAsk(null); return preview(title, body); }} />

      <ScriptConfirm pending={pending} onClose={() => setPending(null)}
        onApplied={() => setNonce(n => n + 1)} />
    </Stack>
  );
}

/// The two or three values one action needs, asked for in one small form rather than in a wizard.
function AskDialog({ ask, principals, onClose, onAsk }: {
  ask: Ask | null;
  principals: DbPrincipalDto[];
  onClose: () => void;
  onAsk: (title: string, body: Parameters<typeof previewUserChange>[1]) => void;
}) {
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("");
  const [privilege, setPrivilege] = useState("SELECT");
  const [target, setTarget] = useState("");
  const [canLogin, setCanLogin] = useState(true);

  useEffect(() => {
    setName("");
    setPassword("");
    setRole(principals.find(p => p.isRole)?.name ?? "");
    setPrivilege("SELECT");
    setTarget("");
    setCanLogin(true);
  }, [ask, principals]);

  if (!ask) return null;

  const submit = () => {
    if (ask.kind === "create") {
      const action: SecurityAction = "create";
      onAsk(`Create ${ask.role ? "role" : "account"} ${name}`,
        { user: name, action, password, role: ask.role, canLogin });
      return;
    }

    if (ask.kind === "password") {
      onAsk(`New password for ${ask.user}`, { user: ask.user, action: "password", password });
      return;
    }

    if (ask.kind === "membership") {
      onAsk(`${ask.grant ? "Put" : "Take"} ${ask.user} ${ask.grant ? "in" : "out of"} ${role}`,
        { user: role, action: ask.grant ? "grant-role" : "revoke-role", member: ask.user });
      return;
    }

    onAsk(`${ask.grant ? "Grant" : "Revoke"} ${privilege} on ${target}`,
      { user: ask.user, action: ask.grant ? "grant" : "revoke", privilege, target });
  };

  return (
    <Modal opened onClose={onClose} title={`${TITLES[ask.kind]}${ask.kind === "create"
      ? ask.role ? " role" : " account"
      : ""}`}>
      <Stack gap="xs">
        {ask.kind === "create" && (
          <>
            <TextInput size="xs" label="Name" value={name} data-autofocus
              onChange={event => setName(event.currentTarget.value)} />
            {!ask.role && (
              <>
                <TextInput size="xs" label="Password" type="password" value={password}
                  onChange={event => setPassword(event.currentTarget.value)} />
                <Switch size="xs" label="May sign in" checked={canLogin}
                  onChange={event => setCanLogin(event.currentTarget.checked)} />
              </>
            )}
          </>
        )}

        {ask.kind === "password" && (
          <TextInput size="xs" label={`New password for ${ask.user}`} type="password" value={password}
            data-autofocus onChange={event => setPassword(event.currentTarget.value)} />
        )}

        {ask.kind === "membership" && (
          <TextInput size="xs" label="Role" value={role} data-autofocus
            placeholder="the role to put it in"
            onChange={event => setRole(event.currentTarget.value)} />
        )}

        {ask.kind === "privilege" && (
          <>
            <TextInput size="xs" label="Privilege" value={privilege} data-autofocus
              placeholder="SELECT, INSERT"
              onChange={event => setPrivilege(event.currentTarget.value)} />
            <TextInput size="xs" label="On" value={target}
              placeholder="orders, or ALL TABLES IN SCHEMA public"
              onChange={event => setTarget(event.currentTarget.value)} />
          </>
        )}

        <Group justify="flex-end" mt="xs">
          <Button size="xs" variant="default" onClick={onClose}>Cancel</Button>
          <Button size="xs" onClick={submit}>Show the statement…</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
