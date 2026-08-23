import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Loader, ScrollArea, Select, Stack, Switch, Table, Text,
  TextInput, Tooltip,
} from "@mantine/core";
import { IconPlus, IconTrash, IconUnlink } from "@tabler/icons-react";
import {
  objectPartitions, objectPolicies, partitionStatement, policyStatement, securityStatement,
  type PartitioningDto, type RowSecurityDto,
} from "../api";
import { formatBytes } from "../redis/format";

const COMMANDS = ["ALL", "SELECT", "INSERT", "UPDATE", "DELETE"];

/// Row-level security: whether it is on, and what the policies say. Every change is a statement the
/// studio hands to the editor — a policy is SQL, and reading it before it runs is the point.
export function PoliciesTab({ connectionId, objectRef, onScript }: {
  connectionId: string;
  objectRef: string;
  onScript?: (sql: string) => void;
}) {
  const [state, setState] = useState<RowSecurityDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [command, setCommand] = useState<string | null>("SELECT");
  const [roles, setRoles] = useState("");
  const [using, setUsing] = useState("");

  useEffect(() => {
    let cancelled = false;

    objectPolicies(connectionId, objectRef)
      .then(found => { if (!cancelled) { setState(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setState(null); };
  }, [connectionId, objectRef]);

  const script = async (work: Promise<{ sql: string }>) => {
    try {
      onScript?.((await work).sql);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!state) return <Loader size="xs" m="xs" />;

  if (!state.supported)
    return (
      <Text size="xs" c="dimmed" p="xs">
        Row-level security is a PostgreSQL feature; this engine has none.
      </Text>
    );

  return (
    <ScrollArea h="calc(100vh - 160px)">
      <Stack gap="sm" p="xs">
        <Group gap="xs">
          <Badge variant="light" color={state.enabled ? "green" : "gray"}>
            {state.enabled ? "row security on" : "row security off"}
          </Badge>
          {state.forced && <Badge variant="light" color="orange">forced for the owner too</Badge>}
          {onScript && (
            <Button size="compact-xs" variant="default"
              onClick={() => script(securityStatement(connectionId, objectRef, !state.enabled, false))}>
              {state.enabled ? "Turn it off…" : "Turn it on…"}
            </Button>
          )}
        </Group>

        {state.enabled && state.policies.length === 0 && (
          <Alert color="orange" p="xs">
            <Text size="xs">
              Security is on and there is no policy, so this table returns nothing to anybody but its
              owner. That is a fine default and a terrible surprise.
            </Text>
          </Alert>
        )}

        <Table fz="xs" striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Policy</Table.Th>
              <Table.Th>For</Table.Th>
              <Table.Th>To</Table.Th>
              <Table.Th>Expression</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {state.policies.map(policy => (
              <Table.Tr key={policy.name}>
                <Table.Td>
                  <Group gap={4} wrap="nowrap">
                    {policy.name}
                    {!policy.permissive && (
                      <Badge size="xs" variant="light" color="orange">restrictive</Badge>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td>{policy.command}</Table.Td>
                <Table.Td>{policy.roles}</Table.Td>
                <Table.Td>
                  <Text size="10px" ff="monospace" style={{ whiteSpace: "pre-wrap" }}>
                    {[policy.using && `USING ${policy.using}`,
                      policy.check && `CHECK ${policy.check}`].filter(Boolean).join("\n")}
                  </Text>
                </Table.Td>
                <Table.Td w={36}>
                  {onScript && (
                    <Tooltip label="Builds the DROP POLICY and shows it before it runs">
                      <ActionIcon size="sm" variant="subtle" color="red" aria-label="Drop policy"
                        onClick={() => script(policyStatement(connectionId, objectRef,
                          { name: policy.name, drop: true }))}>
                        <IconTrash size={13} />
                      </ActionIcon>
                    </Tooltip>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        {onScript && (
          <Stack gap={4}>
            <Text size="xs" fw={600}>New policy</Text>
            <Group gap={4} align="flex-end" wrap="nowrap">
              <TextInput size="xs" w={140} label="Name" value={name}
                onChange={e => setName(e.currentTarget.value)} />
              <Select size="xs" w={110} label="For" data={COMMANDS} value={command}
                onChange={setCommand} allowDeselect={false} />
              <TextInput size="xs" w={140} label="To (roles)" placeholder="public"
                value={roles} onChange={e => setRoles(e.currentTarget.value)} />
              <TextInput size="xs" flex={1} label="USING / WITH CHECK"
                placeholder="tenant_id = current_setting('app.tenant')::int"
                value={using} onChange={e => setUsing(e.currentTarget.value)} />
              <Tooltip label="Builds the CREATE POLICY and shows it before it runs">
                <ActionIcon size="lg" variant="light" aria-label="Create policy"
                  disabled={!name.trim()}
                  onClick={() => script(policyStatement(connectionId, objectRef, {
                    name: name.trim(),
                    command: command ?? "ALL",
                    roles: roles.trim() || undefined,
                    // A SELECT policy filters rows; the others check what is written. Sending the
                    // expression as both would be wrong for exactly one of them.
                    using: command === "INSERT" ? undefined : using.trim() || undefined,
                    check: command === "SELECT" ? undefined : using.trim() || undefined,
                  }))}>
                  <IconPlus size={15} />
                </ActionIcon>
              </Tooltip>
            </Group>
          </Stack>
        )}
      </Stack>
    </ScrollArea>
  );
}

/// How a partitioned table is cut up, what each piece costs, and the two statements that move a
/// piece in or out.
export function PartitionsTab({ connectionId, objectRef, onScript }: {
  connectionId: string;
  objectRef: string;
  onScript?: (sql: string) => void;
}) {
  const [state, setState] = useState<PartitioningDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attach, setAttach] = useState("");
  const [bound, setBound] = useState("");
  const [concurrently, setConcurrently] = useState(false);

  useEffect(() => {
    let cancelled = false;

    objectPartitions(connectionId, objectRef)
      .then(found => { if (!cancelled) { setState(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setState(null); };
  }, [connectionId, objectRef]);

  const script = async (work: Promise<{ sql: string }>) => {
    try {
      onScript?.((await work).sql);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!state) return <Loader size="xs" m="xs" />;

  if (!state.supported)
    return <Text size="xs" c="dimmed" p="xs">This engine has no partitioning the studio reads.</Text>;

  if (!state.partitioned)
    return <Text size="xs" c="dimmed" p="xs">This table is not partitioned.</Text>;

  return (
    <ScrollArea h="calc(100vh - 160px)">
      <Stack gap="sm" p="xs">
        <Group gap="xs">
          <Badge variant="light">{state.strategy}</Badge>
          <Text size="xs" ff="monospace">{state.key}</Text>
          <Text size="xs" c="dimmed">{state.partitions.length} partitions</Text>
        </Group>

        <Table fz="xs" striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Partition</Table.Th>
              <Table.Th>Bound</Table.Th>
              <Table.Th>Size</Table.Th>
              <Table.Th>Rows</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {state.partitions.map(partition => (
              <Table.Tr key={partition.name}>
                <Table.Td>{partition.name}</Table.Td>
                <Table.Td>
                  <Text size="10px" ff="monospace">{partition.bound}</Text>
                </Table.Td>
                <Table.Td>
                  {partition.sizeBytes === null ? "—" : formatBytes(partition.sizeBytes)}
                </Table.Td>
                <Table.Td>{partition.rows ?? "—"}</Table.Td>
                <Table.Td w={36}>
                  {onScript && (
                    <Tooltip label="Detaching leaves the data as a table of its own">
                      <ActionIcon size="sm" variant="subtle" aria-label="Detach"
                        onClick={() => script(partitionStatement(connectionId, objectRef, {
                          partition: partition.name, detach: true, concurrently,
                        }))}>
                        <IconUnlink size={13} />
                      </ActionIcon>
                    </Tooltip>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        {onScript && (
          <Stack gap={4}>
            <Text size="xs" fw={600}>Attach an existing table</Text>
            <Group gap={4} align="flex-end" wrap="nowrap">
              <TextInput size="xs" w={180} label="Table" value={attach}
                onChange={e => setAttach(e.currentTarget.value)} />
              <TextInput size="xs" flex={1} label="Bound"
                placeholder="FOR VALUES FROM ('2026-03-01') TO ('2026-04-01')"
                value={bound} onChange={e => setBound(e.currentTarget.value)} />
              <Tooltip label="Builds the ATTACH PARTITION and shows it before it runs">
                <ActionIcon size="lg" variant="light" aria-label="Attach"
                  disabled={!attach.trim() || !bound.trim()}
                  onClick={() => script(partitionStatement(connectionId, objectRef, {
                    partition: attach.trim(), bound: bound.trim(),
                  }))}>
                  <IconPlus size={15} />
                </ActionIcon>
              </Tooltip>
            </Group>
            <Tooltip label="Detaching concurrently does not block reads, and cannot run in a transaction">
              <Switch size="xs" label="detach concurrently" checked={concurrently}
                onChange={e => setConcurrently(e.currentTarget.checked)} />
            </Tooltip>
          </Stack>
        )}
      </Stack>
    </ScrollArea>
  );
}
