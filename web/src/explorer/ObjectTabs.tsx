import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Code, CopyButton, Group, Loader, ScrollArea, Select, Stack,
  Table, Text, TextInput, Tooltip,
} from "@mantine/core";
import { IconCheck, IconCopy, IconExternalLink, IconPlus, IconTrash } from "@tabler/icons-react";
import {
  objectDependencies, objectDdl, objectPrivileges, objectStatistics, privilegeStatement,
  type ObjectPrivilegesDto, type ObjectStatisticsDto,
} from "../api";
import { formatBytes } from "../redis/format";

/// What a table costs and who reads it — the questions before "should I add an index" and "why is
/// this table 40 GB". Empty on an engine that cannot answer, rather than a table of blanks.
export function StatisticsTab({ connectionId, objectRef }: { connectionId: string; objectRef: string }) {
  const [stats, setStats] = useState<ObjectStatisticsDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    objectStatistics(connectionId, objectRef)
      .then(found => { if (!cancelled) { setStats(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    // Cleared on the way out rather than on the way in, so switching objects does not render the
    // previous one's numbers under the new one's name.
    return () => { cancelled = true; setStats(null); };
  }, [connectionId, objectRef]);

  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!stats) return <Loader size="xs" m="xs" />;

  if (!stats.supported)
    return (
      <Text size="xs" c="dimmed" p="xs">
        This engine keeps no statistics the studio can read. Size and row counts are on the
        <b> Info</b> tab where the engine reports them.
      </Text>
    );

  return (
    <ScrollArea h="calc(100vh - 160px)">
      <Stack gap="xs" p="xs">
        <Table fz="xs" striped>
          <Table.Tbody>
            {stats.table.map(row => (
              <Table.Tr key={row.name}>
                <Table.Td w={190}>{row.name}</Table.Td>
                <Table.Td>
                  <Text size="xs" fw={row.kind === "size" ? 600 : undefined}>
                    {row.value ?? "—"}
                  </Text>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        {stats.indexes.length > 0 && (
          <>
            <Text size="xs" fw={600} mt="xs">Indexes</Text>
            <Table fz="xs" striped>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Name</Table.Th>
                  <Table.Th>Size</Table.Th>
                  <Table.Th>Scans</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {stats.indexes.map(index => (
                  <Table.Tr key={index.name}>
                    <Table.Td>
                      <Group gap={4} wrap="nowrap">
                        {index.name}
                        {index.primary && <Badge size="xs" variant="light">PK</Badge>}
                        {index.unique && !index.primary && (
                          <Badge size="xs" variant="light" color="gray">unique</Badge>
                        )}
                      </Group>
                    </Table.Td>
                    <Table.Td>{index.sizeBytes === null ? "—" : formatBytes(index.sizeBytes)}</Table.Td>
                    <Table.Td>
                      {/* Zero scans on a non-unique index is the clearest "delete me" a database
                          ever gives you. */}
                      {index.scans === null
                        ? "—"
                        : index.scans === 0 && !index.primary
                          ? <Text size="xs" c="orange">0 — nothing reads it</Text>
                          : index.scans}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </>
        )}
      </Stack>
    </ScrollArea>
  );
}

/// What breaks if this changes, and what this needs. The question before every DROP.
export function DependenciesTab({ connectionId, objectRef, onOpen }: {
  connectionId: string;
  objectRef: string;
  onOpen?: (name: string) => void;
}) {
  const [report, setReport] = useState<
    { dependsOn: string[]; usedBy: string[]; bestEffort: boolean } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    objectDependencies(connectionId, objectRef)
      .then(found => { if (!cancelled) { setReport(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setReport(null); };
  }, [connectionId, objectRef]);

  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!report) return <Loader size="xs" m="xs" />;

  const list = (names: string[], empty: string) => names.length === 0
    ? <Text size="xs" c="dimmed">{empty}</Text>
    : (
      <Group gap={4}>
        {names.map(name => (
          <Badge key={name} size="sm" variant="light"
            style={onOpen ? { cursor: "pointer" } : undefined}
            rightSection={onOpen ? <IconExternalLink size={10} /> : undefined}
            onClick={onOpen ? () => onOpen(name) : undefined}>
            {name}
          </Badge>
        ))}
      </Group>
    );

  return (
    <ScrollArea h="calc(100vh - 160px)">
      <Stack gap="sm" p="xs">
        <Stack gap={4}>
          <Text size="xs" fw={600}>Used by — these break if this changes</Text>
          {list(report.usedBy, "nothing found")}
        </Stack>

        <Stack gap={4}>
          <Text size="xs" fw={600}>Depends on — this breaks if those change</Text>
          {list(report.dependsOn, "nothing found")}
        </Stack>

        {report.bestEffort && (
          <Text size="xs" c="dimmed">
            This engine has no dependency catalogue, so the answer is a search rather than a fact.
          </Text>
        )}
      </Stack>
    </ScrollArea>
  );
}

/// The object as SQL. The tab people open to copy a table into a migration.
export function SqlTab({ connectionId, objectRef, onOpenInEditor }: {
  connectionId: string;
  objectRef: string;
  onOpenInEditor?: (sql: string) => void;
}) {
  const [sql, setSql] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    objectDdl(connectionId, objectRef)
      .then(found => {
        if (cancelled) return;
        setSql(found.create ?? null);
        setError(found.create ? null : "this engine does not hand over a definition for this object");
      })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setSql(null); };
  }, [connectionId, objectRef]);

  if (error) return <Text size="xs" c="dimmed" p="xs">{error}</Text>;
  if (!sql) return <Loader size="xs" m="xs" />;

  return (
    <Stack gap={4} p="xs" h="calc(100vh - 160px)">
      <Group gap="xs">
        <CopyButton value={sql}>
          {({ copied, copy }) => (
            <Button size="compact-xs" variant="light" onClick={copy}
              leftSection={copied ? <IconCheck size={13} /> : <IconCopy size={13} />}>
              {copied ? "copied" : "copy"}
            </Button>
          )}
        </CopyButton>
        {onOpenInEditor && (
          <Button size="compact-xs" variant="default" onClick={() => onOpenInEditor(sql)}>
            Open in a query tab
          </Button>
        )}
      </Group>
      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <Code block style={{ whiteSpace: "pre" }}>{sql}</Code>
      </ScrollArea>
    </Stack>
  );
}

/// Who may do what to this object, and the statement that changes it. The statement goes through
/// the same preview as any other change — a GRANT is a change like any other.
export function PrivilegesTab({ connectionId, objectRef, onScript }: {
  connectionId: string;
  objectRef: string;
  onScript?: (sql: string) => void;
}) {
  const [state, setState] = useState<ObjectPrivilegesDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [grantee, setGrantee] = useState("");
  const [privilege, setPrivilege] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    let cancelled = false;

    objectPrivileges(connectionId, objectRef)
      .then(found => {
        if (cancelled) return;
        setState(found);
        setPrivilege(found.privileges[0] ?? null);
        setError(null);
      })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setState(null); };
  }, [connectionId, objectRef, nonce]);

  const build = async (revoke: boolean, who: string, what: string) => {
    try {
      const built = await privilegeStatement(connectionId, objectRef, who, what, revoke);
      onScript?.(built.sql);
      // The preview applies it; re-reading after it closes is the caller's business, so a nudge
      // here keeps the list honest without polling.
      setNonce(n => n + 1);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  if (error) return <Text size="xs" c="red" p="xs">{error}</Text>;
  if (!state) return <Loader size="xs" m="xs" />;

  if (!state.supported)
    return (
      <Text size="xs" c="dimmed" p="xs">
        This engine has no per-object privileges the studio can read.
      </Text>
    );

  return (
    <ScrollArea h="calc(100vh - 160px)">
      <Stack gap="xs" p="xs">
        {onScript && (
          <Group gap={4} align="flex-end" wrap="nowrap">
            <TextInput size="xs" flex={1} placeholder="role or user" label="Grant to"
              value={grantee} onChange={e => setGrantee(e.currentTarget.value)} />
            <Select size="xs" w={150} data={state.privileges} value={privilege}
              onChange={setPrivilege} allowDeselect={false} />
            <Tooltip label="Builds the GRANT and shows it before it runs">
              <ActionIcon size="lg" variant="light" aria-label="Grant"
                disabled={!grantee.trim() || !privilege}
                onClick={() => build(false, grantee.trim(), privilege!)}>
                <IconPlus size={15} />
              </ActionIcon>
            </Tooltip>
          </Group>
        )}

        <Table fz="xs" striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Grantee</Table.Th>
              <Table.Th>Privilege</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {state.grants.map(grant => (
              <Table.Tr key={`${grant.grantee}:${grant.privilege}`}>
                <Table.Td>{grant.grantee}</Table.Td>
                <Table.Td>
                  <Group gap={4} wrap="nowrap">
                    {grant.privilege}
                    {grant.grantable && (
                      <Badge size="xs" variant="light" color="gray">may pass on</Badge>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td w={40}>
                  {onScript && (
                    <Tooltip label="Builds the REVOKE and shows it before it runs">
                      <ActionIcon size="sm" variant="subtle" color="red" aria-label="Revoke"
                        onClick={() => build(true, grant.grantee, grant.privilege)}>
                        <IconTrash size={13} />
                      </ActionIcon>
                    </Tooltip>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        {state.grants.length === 0 && (
          <Alert p="xs" color="gray">
            <Text size="xs">
              Nobody has an explicit grant here. The owner and any superuser still reach it — this
              lists grants, not effective access.
            </Text>
          </Alert>
        )}
      </Stack>
    </ScrollArea>
  );
}
