import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import {
  Alert, Badge, Center, Code, Group, Loader, ScrollArea, Stack, Table, Text, Title,
} from "@mantine/core";
import { sharedResult, type SharedResultDto } from "../api";
import { BrandLinks } from "../components/BrandLinks";

/// A result somebody kept, as it was. No connection, no query to run, nothing to click: the point of
/// a link is that it shows what the sender saw, to somebody who may not have the studio at all.
export function SharePage() {
  const { id = "" } = useParams();
  const [shared, setShared] = useState<SharedResultDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    sharedResult(id)
      .then(found => { setShared(found); setError(null); })
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  }, [id]);

  useEffect(() => {
    document.title = shared ? `Shared result · ${shared.connectionName}` : "Shared result";
  }, [shared]);

  if (error) {
    return (
      <Center h="100vh" p="lg">
        <Alert color="red" title="This link does not work" maw={520}>
          <Text size="sm">{error}</Text>
          <Text size="xs" c="dimmed" mt="xs">
            Shared results expire, and an expired link says nothing about what it used to show.
          </Text>
        </Alert>
      </Center>
    );
  }

  if (!shared) return <Center h="100vh"><Loader /></Center>;

  return (
    <Stack gap="sm" p="md" h="100vh" style={{ minHeight: 0 }}>
      <Group justify="space-between" wrap="nowrap">
        <Stack gap={2}>
          <Title order={4}>{shared.connectionName}</Title>
          <Text size="xs" c="dimmed">
            {shared.rows.length} rows{shared.truncated ? " (truncated)" : ""}
            {shared.by ? ` · shared by ${shared.by}` : ""}
            {` · ${new Date(shared.at).toLocaleString()}`}
            {` · expires ${new Date(shared.expiresAt).toLocaleString()}`}
          </Text>
        </Stack>
        <Group gap="xs" wrap="nowrap">
          <Badge variant="light">snapshot</Badge>
          <BrandLinks size={16} />
        </Group>
      </Group>

      <Code block style={{ whiteSpace: "pre-wrap" }}>{shared.sql}</Code>

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <Table striped withTableBorder stickyHeader>
          <Table.Thead>
            <Table.Tr>
              {shared.columns.map(column => (
                <Table.Th key={column}>{column}</Table.Th>
              ))}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {shared.rows.map((row, index) => (
              <Table.Tr key={index}>
                {row.map((cell, position) => (
                  <Table.Td key={position}>
                    <Text size="xs" ff={cell === null ? undefined : "monospace"}
                      c={cell === null ? "dimmed" : undefined}>
                      {cell === null ? "null" : cell}
                    </Text>
                  </Table.Td>
                ))}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>

      {shared.truncated && (
        <Text size="xs" c="dimmed">
          Only the first rows were kept — a link is a snapshot, not an export.
        </Text>
      )}
    </Stack>
  );
}
