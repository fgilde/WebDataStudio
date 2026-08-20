import { useEffect, useState } from "react";
import { Badge, Group, Table, Text } from "@mantine/core";
import { replicationState, type ReplicaStateDto } from "../api";
import { formatBytes } from "../redis/format";

/// Replicas and how far behind they are — the first question in any incident that is not about the
/// primary. A server with no replicas says so rather than showing an empty table with no reason.
export function Replication({ connectionId }: { connectionId: string }) {
  const [replicas, setReplicas] = useState<ReplicaStateDto[] | null>(null);

  useEffect(() => {
    let cancelled = false;

    replicationState(connectionId)
      .then(state => { if (!cancelled) setReplicas(state); })
      .catch(() => { if (!cancelled) setReplicas([]); });

    return () => { cancelled = true; };
  }, [connectionId]);

  if (replicas === null) return <Text size="xs" c="dimmed" p="sm">Reading…</Text>;

  if (replicas.length === 0)
    return (
      <Text size="xs" c="dimmed" p="sm">
        This server reports no replicas. Either it has none, or the account cannot read the
        replication view.
      </Text>
    );

  return (
    <Table fz="xs" striped>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Replica</Table.Th><Table.Th w={90}>Role</Table.Th>
          <Table.Th w={120}>State</Table.Th><Table.Th w={110}>Lag</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {replicas.map(replica => (
          <Table.Tr key={replica.name}>
            <Table.Td>{replica.name}</Table.Td>
            <Table.Td><Badge size="xs" variant="light">{replica.role}</Badge></Table.Td>
            <Table.Td>
              <Badge size="xs"
                color={replica.state.toLowerCase().startsWith("stream")
                  || replica.state.toLowerCase() === "on"
                  || replica.state.toLowerCase().startsWith("synchron") ? "green" : "orange"}>
                {replica.state}
              </Badge>
            </Table.Td>
            <Table.Td>
              <Group gap={4} wrap="nowrap">
                {replica.lagBytes !== null ? <Text size="xs">{formatBytes(replica.lagBytes)}</Text> : null}
                {replica.lagSeconds !== null && replica.lagSeconds > 0
                  ? <Text size="10px" c="orange">{replica.lagSeconds}s</Text>
                  : null}
                {replica.lagBytes === null && replica.lagSeconds === null
                  ? <Text size="10px" c="dimmed">not reported</Text>
                  : null}
              </Group>
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}
