import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Code, Group, Loader, Modal, ScrollArea, Stack, Table, Text, Tooltip,
} from "@mantine/core";
import { IconCheck, IconCopy, IconEye, IconEyeOff } from "@tabler/icons-react";
import { connectionProperties, revealConnectionString, type ConnectionPropertiesDto } from "../api";
import { SchemaScopePicker } from "./SchemaScopePicker";

/// The capability flags worth showing in plain words. The rest of them describe internals a
/// reader of this dialog has no use for.
const CAPABILITY_LABELS: [string, string][] = [
  ["sql", "SQL"],
  ["multiSchema", "Schemas"],
  ["multiDatabase", "Several databases"],
  ["transactions", "Transactions"],
  ["ddl", "DDL"],
  ["views", "Views"],
  ["materializedViews", "Materialised views"],
  ["storedProcedures", "Stored procedures"],
  ["triggers", "Triggers"],
  ["sequences", "Sequences"],
  ["foreignKeys", "Foreign keys"],
  ["partialIndexes", "Partial indexes"],
  ["includeColumns", "Include columns"],
  ["fullTextIndexes", "Full-text indexes"],
  ["estimatedPlan", "Estimated plan"],
  ["actualPlan", "Actual plan"],
  ["backup", "Backup"],
  ["restore", "Restore"],
  ["userManagement", "User management"],
  ["sessionList", "Sessions"],
  ["killSession", "Kill session"],
  ["serverStats", "Server metrics"],
  ["slowQueryLog", "Slow queries"],
  ["systemCommands", "Maintenance commands"],
];

export function PropertiesDialog({ connectionId, label, onClose, onScopeChanged }: {
  connectionId: string | null;
  label: string;
  onClose: () => void;
  /// The tree is re-read when the schemas in scope change.
  onScopeChanged?: () => void;
}) {
  const [data, setData] = useState<ConnectionPropertiesDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [revealed, setRevealed] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!connectionId) return;

    // Reopening has to start over: a revealed password must not survive a close.
    setData(null);
    setError(null);
    setRevealed(null);
    setCopied(false);

    let cancelled = false;
    connectionProperties(connectionId)
      .then(value => { if (!cancelled) setData(value); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [connectionId]);

  const shown = revealed ?? data?.connectionString ?? "";

  const copy = async (withPassword: boolean) => {
    if (!connectionId || !data) return;

    const value = withPassword ? await revealConnectionString(connectionId) : data.connectionString;
    await navigator.clipboard.writeText(value);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  const groups = [...new Set((data?.properties ?? []).map(p => p.group))];

  return (
    <Modal opened={connectionId !== null} onClose={onClose} size="lg" title={`Properties of ${label}`}>
      {error ? <Alert color="red" variant="light">{error}</Alert> : null}
      {!data && !error ? <Loader size="sm" /> : null}

      {data ? (
        <Stack gap="md">
          {!data.reachable ? (
            <Alert color="yellow" variant="light" title="The server did not answer">
              {data.error}
            </Alert>
          ) : null}

          {/* Which schemas this connection reads at all: on a large server that is the difference
              between a tree that opens and one that takes a minute. */}
          {connectionId && <SchemaScopePicker connectionId={connectionId} onChanged={onScopeChanged} />}

          <div>
            <Group gap={6} mb={4} justify="space-between">
              <Text size="sm" fw={600}>Connection string</Text>
              <Group gap={2}>
                {data.hasPassword ? (
                  <Tooltip label={revealed ? "Hide the password" : "Show the password"}>
                    <ActionIcon size="sm" variant="subtle"
                      aria-label={revealed ? "Hide the password" : "Show the password"}
                      onClick={async () => {
                        if (revealed) { setRevealed(null); return; }
                        if (connectionId) setRevealed(await revealConnectionString(connectionId));
                      }}>
                      {revealed ? <IconEyeOff size={15} /> : <IconEye size={15} />}
                    </ActionIcon>
                  </Tooltip>
                ) : null}

                <Tooltip label={data.hasPassword ? "Copy without the password" : "Copy"}>
                  <ActionIcon size="sm" variant="subtle" aria-label="Copy connection string"
                    onClick={() => copy(false)}>
                    {copied ? <IconCheck size={15} /> : <IconCopy size={15} />}
                  </ActionIcon>
                </Tooltip>

                {data.hasPassword ? (
                  <Tooltip label="Copy including the password">
                    <ActionIcon size="sm" variant="subtle" color="orange"
                      aria-label="Copy with the password" onClick={() => copy(true)}>
                      <IconCopy size={15} />
                    </ActionIcon>
                  </Tooltip>
                ) : null}
              </Group>
            </Group>

            <Code block fz="xs" style={{ whiteSpace: "pre-wrap", wordBreak: "break-all" }}>
              {shown}
            </Code>

            {data.hasPassword && !revealed ? (
              <Text size="10px" c="dimmed" mt={4}>
                The password is hidden. Reveal or copy it with the buttons above.
              </Text>
            ) : null}
          </div>

          {groups.map(group => (
            <div key={group}>
              <Text size="sm" fw={600} mb={4}>{group}</Text>
              <Table fz="xs" striped withRowBorders={false}>
                <Table.Tbody>
                  {data.properties.filter(p => p.group === group).map(p => (
                    <Table.Tr key={`${group}-${p.name}`}>
                      <Table.Td w={160}><Text size="xs" c="dimmed">{p.name}</Text></Table.Td>
                      <Table.Td style={{ wordBreak: "break-word" }}>{p.value}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </div>
          ))}

          <div>
            <Text size="sm" fw={600} mb={6}>What this engine supports</Text>
            <ScrollArea.Autosize mah={160}>
              <Group gap={4}>
                {CAPABILITY_LABELS.filter(([key]) => key in data.capabilities).map(([key, text]) => (
                  <Badge key={key} size="xs"
                    variant={data.capabilities[key] ? "light" : "outline"}
                    color={data.capabilities[key] ? "teal" : "gray"}
                    // Faded rather than merely outlined: at badge size the two variants alone read
                    // as the same thing.
                    style={{ opacity: data.capabilities[key] ? 1 : 0.4 }}>
                    {text}
                  </Badge>
                ))}
              </Group>
            </ScrollArea.Autosize>
          </div>
        </Stack>
      ) : null}
    </Modal>
  );
}
