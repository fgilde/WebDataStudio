import { useCallback, useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Checkbox, Code, Group, Loader, Modal, ScrollArea,
  Stack, Table, Tabs, Text, TextInput, Tooltip,
} from "@mantine/core";
import { IconPlayerStop, IconRefresh, IconTrash } from "@tabler/icons-react";
import {
  applyUserChange, createDatabase, downloadBackup, dropDatabase, killSession, listDatabases,
  listSessions, listUsers, previewUserChange, restoreBackup, runSystemCommand, serverLog,
  slowQueries, systemCommands, serverStats,
  type DatabaseDto, type SessionDto, type SystemCommandDto,
} from "../api";
import { Overview } from "./Overview";
import { SizeTreemap } from "./SizeTreemap";
import { Replication } from "./Replication";

/// Every tab here is allowed to be empty or unavailable: the server tells us which of these an
/// engine supports, and "not supported" is an answer, not an error.
function useAsync<T>(load: () => Promise<T>, deps: unknown[]) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = useCallback(() => {
    setBusy(true);
    setError(null);
    load().then(setData).catch(e => { setData(null); setError(e.message); }).finally(() => setBusy(false));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  useEffect(() => { run(); }, [run]);
  return { data, error, busy, reload: run };
}

function Maintenance({ connectionId }: { connectionId: string }) {
  const { data, error, busy, reload } = useAsync(() => systemCommands(connectionId), [connectionId]);
  const [target, setTarget] = useState("");
  const [result, setResult] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [pending, setPending] = useState<SystemCommandDto | null>(null);

  const run = (command: SystemCommandDto) => {
    setResult(null);
    setFailure(null);
    runSystemCommand(connectionId, command.id, target || undefined)
      .then(r => setResult(r.executed))
      .catch(e => setFailure(e.message))
      .finally(() => setPending(null));
  };

  if (busy) return <Loader size="xs" m="sm" />;
  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;
  if (!data?.length) return <Text size="xs" c="dimmed" p="xs">This engine has no maintenance commands.</Text>;

  return (
    <Stack gap="xs" p="xs">
      <Group gap={6}>
        <TextInput size="xs" flex={1} placeholder="Target table (where a command needs one)"
          value={target} onChange={e => setTarget(e.currentTarget.value)} aria-label="Target table" />
        <ActionIcon size="sm" variant="subtle" aria-label="Reload commands" onClick={reload}>
          <IconRefresh size={15} />
        </ActionIcon>
      </Group>

      {data.map(command => (
        <Group key={command.id} gap={8} wrap="nowrap">
          <Button size="compact-xs" variant={command.destructive ? "light" : "default"}
            color={command.destructive ? "red" : undefined}
            disabled={command.needsTarget && !target}
            onClick={() => (command.destructive ? setPending(command) : run(command))}>
            {command.label}
          </Button>
          <Text size="xs" c="dimmed">{command.description}</Text>
        </Group>
      ))}

      {result ? <Alert color="green" variant="light"><Code>{result}</Code></Alert> : null}
      {failure ? <Alert color="red" variant="light">{failure}</Alert> : null}

      <Modal opened={pending !== null} onClose={() => setPending(null)} title={pending?.label ?? ""}>
        <Stack gap="sm">
          <Text size="sm">{pending?.description}</Text>
          <Text size="sm" fw={600}>Run this against {target || "the database"}?</Text>
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setPending(null)}>Cancel</Button>
            <Button size="xs" color="red" onClick={() => pending && run(pending)}>Run</Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function Sessions({ connectionId }: { connectionId: string }) {
  const { data, error, busy, reload } = useAsync(() => listSessions(connectionId), [connectionId]);
  const [killing, setKilling] = useState<SessionDto | null>(null);

  if (busy) return <Loader size="xs" m="sm" />;
  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;

  return (
    <Stack gap={4} p="xs">
      <Group justify="space-between">
        <Text size="xs" c="dimmed">{data?.length ?? 0} sessions</Text>
        <ActionIcon size="sm" variant="subtle" aria-label="Reload sessions" onClick={reload}>
          <IconRefresh size={15} />
        </ActionIcon>
      </Group>

      <ScrollArea h={320}>
        <Table striped highlightOnHover fz="xs" stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Id</Table.Th><Table.Th>User</Table.Th><Table.Th>Database</Table.Th>
              <Table.Th>State</Table.Th><Table.Th>Query</Table.Th><Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {data?.map(session => (
              <Table.Tr key={session.id}>
                <Table.Td>{session.id}</Table.Td>
                <Table.Td>{session.user}</Table.Td>
                <Table.Td>{session.database}</Table.Td>
                <Table.Td>
                  {session.state}
                  {session.blockedBy ? <Badge size="xs" color="red" ml={4}>blocked by {session.blockedBy}</Badge> : null}
                </Table.Td>
                <Table.Td style={{ maxWidth: 320, overflow: "hidden", textOverflow: "ellipsis" }}>
                  <Tooltip label={session.query} multiline w={420} disabled={!session.query}>
                    <span>{session.query.slice(0, 80)}</span>
                  </Tooltip>
                </Table.Td>
                <Table.Td>
                  <ActionIcon size="sm" variant="subtle" color="red" aria-label={`Kill ${session.id}`}
                    onClick={() => setKilling(session)}>
                    <IconPlayerStop size={14} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>

      <Modal opened={killing !== null} onClose={() => setKilling(null)} title="Terminate session">
        <Stack gap="sm">
          {/* Killing a session rolls back whatever it was doing — worth a second look. */}
          <Text size="sm">Session {killing?.id} of {killing?.user} will be terminated and its
            transaction rolled back.</Text>
          <Code block>{killing?.query || "(idle)"}</Code>
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setKilling(null)}>Cancel</Button>
            <Button size="xs" color="red" onClick={() => {
              if (killing) killSession(connectionId, killing.id).finally(() => { setKilling(null); reload(); });
            }}>Terminate</Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function Databases({ connectionId }: { connectionId: string }) {
  const { data, error, busy, reload } = useAsync(() => listDatabases(connectionId), [connectionId]);
  const [name, setName] = useState("");
  const [dropping, setDropping] = useState<DatabaseDto | null>(null);
  const [confirm, setConfirm] = useState("");
  const [failure, setFailure] = useState<string | null>(null);

  if (busy) return <Loader size="xs" m="sm" />;
  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;

  return (
    <Stack gap={4} p="xs">
      <Group gap={6}>
        <TextInput size="xs" flex={1} placeholder="New database name" value={name}
          onChange={e => setName(e.currentTarget.value)} aria-label="New database name" />
        <Button size="compact-xs" disabled={!name} onClick={() =>
          createDatabase(connectionId, name)
            .then(() => { setName(""); reload(); })
            .catch(e => setFailure(e.message))}>Create</Button>
        <ActionIcon size="sm" variant="subtle" aria-label="Reload databases" onClick={reload}>
          <IconRefresh size={15} />
        </ActionIcon>
      </Group>

      {failure ? <Alert color="red" variant="light">{failure}</Alert> : null}

      <ScrollArea h={300}>
        <Table striped fz="xs">
          <Table.Tbody>
            {data?.map(database => (
              <Table.Tr key={database.name}>
                <Table.Td>{database.name}</Table.Td>
                <Table.Td>
                  {database.sizeBytes ? `${(database.sizeBytes / 1024 / 1024).toFixed(1)} MB` : ""}
                </Table.Td>
                <Table.Td w={40}>
                  <ActionIcon size="sm" variant="subtle" color="red" aria-label={`Drop ${database.name}`}
                    onClick={() => { setDropping(database); setConfirm(""); }}>
                    <IconTrash size={14} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>

      <Modal opened={dropping !== null} onClose={() => setDropping(null)} title="Drop database">
        <Stack gap="sm">
          <Alert color="red" variant="light">
            Dropping <b>{dropping?.name}</b> deletes every table in it. This cannot be undone.
          </Alert>
          {/* Typing the name is the only gate between a click and a lost database. */}
          <TextInput size="xs" label={`Type ${dropping?.name} to confirm`} value={confirm}
            onChange={e => setConfirm(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setDropping(null)}>Cancel</Button>
            <Button size="xs" color="red" disabled={confirm !== dropping?.name} onClick={() => {
              if (dropping) dropDatabase(connectionId, dropping.name)
                .catch(e => setFailure(e.message))
                .finally(() => { setDropping(null); reload(); });
            }}>Drop</Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function Users({ connectionId }: { connectionId: string }) {
  const { data, error, busy, reload } = useAsync(() => listUsers(connectionId), [connectionId]);
  const [user, setUser] = useState("");
  const [password, setPassword] = useState("");
  const [privilege, setPrivilege] = useState("");
  const [target, setTarget] = useState("");
  const [preview, setPreview] = useState<{ hash: string; script: string } | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  if (busy) return <Loader size="xs" m="sm" />;
  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;

  return (
    <Stack gap="xs" p="xs">
      <Group gap={6} align="flex-end">
        <TextInput size="xs" label="User" value={user} onChange={e => setUser(e.currentTarget.value)} />
        <TextInput size="xs" label="Password" type="password" value={password}
          onChange={e => setPassword(e.currentTarget.value)} />
        <TextInput size="xs" label="Privilege" placeholder="SELECT" value={privilege}
          onChange={e => setPrivilege(e.currentTarget.value)} />
        <TextInput size="xs" label="On" placeholder="table" value={target}
          onChange={e => setTarget(e.currentTarget.value)} />
        <Button size="compact-xs" disabled={!user} onClick={() =>
          previewUserChange(connectionId, { user, password, privilege, target })
            .then(setPreview).catch(e => setFailure(e.message))}>Preview</Button>
      </Group>

      {failure ? <Alert color="red" variant="light">{failure}</Alert> : null}

      <ScrollArea h={260}>
        <Table striped fz="xs">
          <Table.Tbody>
            {data?.map(name => <Table.Tr key={name}><Table.Td>{name}</Table.Td></Table.Tr>)}
          </Table.Tbody>
        </Table>
      </ScrollArea>

      {/* The statement is shown before it runs — the same handshake the DDL designer uses. */}
      <Modal opened={preview !== null} onClose={() => setPreview(null)} title="Review the statement">
        <Stack gap="sm">
          <Code block>{preview?.script}</Code>
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setPreview(null)}>Cancel</Button>
            <Button size="xs" onClick={() => {
              if (preview) applyUserChange(connectionId, preview.hash)
                .catch(e => setFailure(e.message))
                .finally(() => { setPreview(null); reload(); });
            }}>Run</Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function Backup({ connectionId, database }: { connectionId: string; database: string }) {
  const [schemaOnly, setSchemaOnly] = useState(false);
  const [dataOnly, setDataOnly] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [confirm, setConfirm] = useState("");

  return (
    <Stack gap="xs" p="xs">
      <Group gap="md">
        <Checkbox size="xs" label="Schema only" checked={schemaOnly}
          onChange={e => { setSchemaOnly(e.currentTarget.checked); setDataOnly(false); }} />
        <Checkbox size="xs" label="Data only" checked={dataOnly}
          onChange={e => { setDataOnly(e.currentTarget.checked); setSchemaOnly(false); }} />
        <Button size="compact-xs" loading={busy} onClick={() => {
          setBusy(true);
          setFailure(null);
          downloadBackup(connectionId, { schemaOnly, dataOnly })
            .catch(e => setFailure(e.message))
            .finally(() => setBusy(false));
        }}>Download backup</Button>
      </Group>

      <Text size="xs" c="dimmed">
        The dump is produced by the engine's own tool and streamed straight to your browser.
      </Text>

      <Group gap={6} align="flex-end">
        <input type="file" aria-label="Backup file"
          onChange={e => setFile(e.currentTarget.files?.[0] ?? null)} />
        <TextInput size="xs" label="Type the database name to confirm" value={confirm}
          onChange={e => setConfirm(e.currentTarget.value)} />
        <Button size="compact-xs" color="red" disabled={!file || !confirm} onClick={() => {
          if (!file) return;
          setBusy(true);
          setFailure(null);
          restoreBackup(connectionId, file, confirm)
            .then(setMessage).catch(e => setFailure(e.message)).finally(() => setBusy(false));
        }}>Restore</Button>
      </Group>

      <Text size="xs" c="dimmed">Restoring overwrites {database || "the target database"}.</Text>

      {message ? <Alert color="green" variant="light">{message}</Alert> : null}
      {failure ? <Alert color="red" variant="light">{failure}</Alert> : null}
    </Stack>
  );
}

function SlowQueries({ connectionId }: { connectionId: string }) {
  const { data, error, busy, reload } = useAsync(() => slowQueries(connectionId), [connectionId]);

  if (busy) return <Loader size="xs" m="sm" />;
  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;
  if (!data?.length)
    return (
      <Text size="xs" c="dimmed" p="xs">
        No slow-query source on this engine, or the extension that provides it is not installed.
      </Text>
    );

  return (
    <Stack gap={4} p="xs">
      <ActionIcon size="sm" variant="subtle" aria-label="Reload slow queries" onClick={reload}>
        <IconRefresh size={15} />
      </ActionIcon>
      <ScrollArea h={340}>
        <Table striped fz="xs" stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Query</Table.Th><Table.Th w={80}>Calls</Table.Th>
              <Table.Th w={110}>Total ms</Table.Th><Table.Th w={110}>Mean ms</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {data.map((entry, index) => (
              <Table.Tr key={index}>
                <Table.Td style={{ fontFamily: "monospace" }}>{entry.query}</Table.Td>
                <Table.Td>{entry.calls}</Table.Td>
                <Table.Td>{Math.round(entry.totalMs)}</Table.Td>
                <Table.Td>{Math.round(entry.meanMs * 100) / 100}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}

function ServerMetrics({ connectionId }: { connectionId: string }) {
  const { data, error, busy, reload } = useAsync(() => serverStats(connectionId), [connectionId]);

  if (busy) return <Loader size="xs" m="sm" />;
  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;

  return (
    <Stack gap={4} p="xs">
      <ActionIcon size="sm" variant="subtle" aria-label="Reload metrics" onClick={reload}>
        <IconRefresh size={15} />
      </ActionIcon>
      <Table striped fz="xs">
        <Table.Tbody>
          {data?.metrics.map(metric => (
            <Table.Tr key={metric.name}>
              <Table.Td>{metric.name}</Table.Td>
              <Table.Td>{metric.value}</Table.Td>
              <Table.Td><Text size="10px" c="dimmed">{metric.detail}</Text></Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>

      {data?.blocking.length ? (
        <>
          <Text size="xs" fw={600}>Blocking chains</Text>
          <Table striped fz="xs">
            <Table.Tbody>
              {data.blocking.map((entry, index) => (
                <Table.Tr key={index}>
                  <Table.Td>{entry.sessionId} blocked by {entry.blockedBy}</Table.Td>
                  <Table.Td>{entry.waitMs} ms</Table.Td>
                  <Table.Td style={{ fontFamily: "monospace" }}>{entry.query.slice(0, 80)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </>
      ) : null}
    </Stack>
  );
}

function Logs({ connectionId }: { connectionId: string }) {
  const { data, error, busy, reload } = useAsync(() => serverLog(connectionId), [connectionId]);

  if (busy) return <Loader size="xs" m="sm" />;
  if (error) return <Alert color="yellow" variant="light" m="xs">{error}</Alert>;
  if (!data?.available)
    return <Text size="xs" c="dimmed" p="xs">{data?.reason ?? "No log available."}</Text>;

  return (
    <Stack gap={4} p="xs">
      <ActionIcon size="sm" variant="subtle" aria-label="Reload log" onClick={reload}>
        <IconRefresh size={15} />
      </ActionIcon>
      <ScrollArea h={340}>
        <Code block fz="xs">{data.lines.join("\n")}</Code>
      </ScrollArea>
    </Stack>
  );
}

export function AdminPanel({ connectionId, database = "" }: { connectionId: string; database?: string }) {
  if (!connectionId) return <Text size="xs" c="dimmed" p="xs">Select a connection first.</Text>;

  return (
    <Tabs defaultValue="overview" keepMounted={false}>
      <Tabs.List>
        <Tabs.Tab value="overview">Overview</Tabs.Tab>
        <Tabs.Tab value="maintenance">Maintenance</Tabs.Tab>
        <Tabs.Tab value="sessions">Sessions</Tabs.Tab>
        <Tabs.Tab value="databases">Databases</Tabs.Tab>
        <Tabs.Tab value="users">Users</Tabs.Tab>
        <Tabs.Tab value="backup">Backup</Tabs.Tab>
        <Tabs.Tab value="metrics">Metrics</Tabs.Tab>
        <Tabs.Tab value="slow">Slow queries</Tabs.Tab>
        <Tabs.Tab value="replication">Replication</Tabs.Tab>
        <Tabs.Tab value="logs">Log</Tabs.Tab>
      </Tabs.List>

      <Tabs.Panel value="maintenance"><Maintenance connectionId={connectionId} /></Tabs.Panel>
      <Tabs.Panel value="sessions"><Sessions connectionId={connectionId} /></Tabs.Panel>
      <Tabs.Panel value="databases">
        {/* The list to act on, and above it where the disk actually went. */}
        <ScrollArea h="100%" p="xs">
          <SizeTreemap connectionId={connectionId} />
          <div style={{ marginTop: 12 }}>
            <Databases connectionId={connectionId} />
          </div>
        </ScrollArea>
      </Tabs.Panel>
      <Tabs.Panel value="users"><Users connectionId={connectionId} /></Tabs.Panel>
      <Tabs.Panel value="backup"><Backup connectionId={connectionId} database={database} /></Tabs.Panel>
      <Tabs.Panel value="overview"><Overview connectionId={connectionId} /></Tabs.Panel>
      <Tabs.Panel value="metrics"><ServerMetrics connectionId={connectionId} /></Tabs.Panel>
      <Tabs.Panel value="replication"><Replication connectionId={connectionId} /></Tabs.Panel>
      <Tabs.Panel value="slow"><SlowQueries connectionId={connectionId} /></Tabs.Panel>
      <Tabs.Panel value="logs"><Logs connectionId={connectionId} /></Tabs.Panel>
    </Tabs>
  );
}
