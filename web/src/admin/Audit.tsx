import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Group, Loader, ScrollArea, Stack, Table, Text, TextInput, Tooltip,
} from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { auditTrail, type AuditEntryDto } from "../api";

/// A status the person reading this cares about: refused is the interesting one.
const colour = (status: number) =>
  status >= 500 ? "red" : status === 401 || status === 403 ? "orange" : status >= 400 ? "yellow" : "gray";

/// Who did what, through this studio.
///
/// The trail is one line per request that changed something or took data out of the building, so it
/// answers the question a log full of HTTP lines answers badly: who exported that, and when.
export function Audit({ connectionId }: {
  /// The connection this panel was opened for. The trail starts filtered to it, because that is
  /// usually the question; clearing the box shows everything.
  connectionId?: string;
}) {
  const [entries, setEntries] = useState<AuditEntryDto[] | null>(null);
  const [enabled, setEnabled] = useState(true);
  const [user, setUser] = useState("");
  const [search, setSearch] = useState("");
  const [conn, setConn] = useState(connectionId ?? "");
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let current = true;

    auditTrail({ user, conn, search, limit: 300 })
      .then(trail => {
        if (!current) return;
        setEntries(trail.entries);
        setEnabled(trail.enabled);
      })
      .catch(e => { if (current) setError(e.message); });

    return () => { current = false; };
  }, [user, conn, search, tick]);

  const reload = () => setTick(value => value + 1);

  if (entries === null && error === null) return <Loader size="xs" m="sm" />;

  return (
    <Stack gap={4} p="xs">
      <Group gap={6} align="flex-end">
        <TextInput size="xs" w={140} label="Who" placeholder="anyone" value={user}
          onChange={e => setUser(e.currentTarget.value)} />
        <TextInput size="xs" w={140} label="Connection" placeholder="any" value={conn}
          onChange={e => setConn(e.currentTarget.value)} />
        <TextInput size="xs" flex={1} label="What" placeholder="export, DROP TABLE, a table name…"
          value={search} onChange={e => setSearch(e.currentTarget.value)} />
        <ActionIcon size="sm" variant="subtle" aria-label="Reload the trail" onClick={reload}>
          <IconRefresh size={15} />
        </ActionIcon>
      </Group>

      {error && <Alert color="red" variant="light">{error}</Alert>}

      {!enabled && (
        <Alert color="yellow" variant="light">
          The trail is turned off for this deployment (WDS_AUDIT=false).
        </Alert>
      )}

      {enabled && entries?.length === 0 && (
        <Text size="xs" c="dimmed">
          Nothing yet. A statement run, an export, a change applied or a request refused each leave
          one line here.
        </Text>
      )}

      <ScrollArea h={360}>
        <Table striped highlightOnHover fz="xs" stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th w={150}>When</Table.Th><Table.Th w={110}>Who</Table.Th>
              <Table.Th w={190}>Action</Table.Th><Table.Th>Detail</Table.Th>
              <Table.Th w={70}>Status</Table.Th><Table.Th w={70}>Took</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {entries?.map(entry => (
              <Table.Tr key={entry.id}>
                <Table.Td>{new Date(entry.at).toLocaleString()}</Table.Td>
                <Table.Td>
                  {entry.user}
                  {entry.role ? <Text span c="dimmed"> ({entry.role})</Text> : null}
                </Table.Td>
                <Table.Td style={{ fontFamily: "monospace" }}>{entry.action}</Table.Td>
                <Table.Td style={{ maxWidth: 380, overflow: "hidden", textOverflow: "ellipsis" }}>
                  <Tooltip label={entry.detail} multiline w={520} disabled={!entry.detail}>
                    <span style={{ fontFamily: "monospace" }}>{entry.detail.slice(0, 120)}</span>
                  </Tooltip>
                  {entry.connectionId
                    ? <Badge size="xs" variant="light" ml={4}>{entry.connectionId}</Badge>
                    : null}
                </Table.Td>
                <Table.Td>
                  <Badge size="xs" color={colour(entry.status)}>{entry.status}</Badge>
                </Table.Td>
                <Table.Td>{entry.elapsedMs} ms</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Stack>
  );
}
