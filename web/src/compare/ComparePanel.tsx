import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Code, Group, List, Loader, ScrollArea, Select, Stack, Table, Tabs,
  Text, TextInput,
} from "@mantine/core";
import {
  compareData, compareSchemas, listConnections,
  type Connection, type DataComparisonDto, type SchemaComparisonDto,
} from "../api";

function useConnections() {
  const [connections, setConnections] = useState<Connection[]>([]);
  useEffect(() => { listConnections().then(setConnections).catch(() => setConnections([])); }, []);
  return connections;
}

const options = (connections: Connection[]) =>
  connections.map(c => ({ value: c.id, label: c.name }));

function SchemaCompare({ initialConnectionId }: { initialConnectionId: string }) {
  const connections = useConnections();
  const [source, setSource] = useState<string | null>(initialConnectionId || null);
  const [target, setTarget] = useState<string | null>(null);
  const [result, setResult] = useState<SchemaComparisonDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = () => {
    if (!source || !target) return;
    setBusy(true);
    setError(null);
    compareSchemas({ sourceConnectionId: source, targetConnectionId: target })
      .then(setResult).catch(e => setError(e.message)).finally(() => setBusy(false));
  };

  return (
    <Stack gap="xs" p="xs">
      <Group gap={6} align="flex-end">
        <Select size="xs" label="Source" data={options(connections)} value={source} onChange={setSource} />
        <Select size="xs" label="Target" data={options(connections)} value={target} onChange={setTarget} />
        <Button size="compact-xs" disabled={!source || !target} loading={busy} onClick={run}>Compare</Button>
      </Group>

      {error ? <Alert color="red" variant="light">{error}</Alert> : null}
      {busy ? <Loader size="xs" /> : null}

      {result ? (
        <Stack gap="xs">
          <Group gap={6}>
            <Badge size="sm" color="green">{result.tablesOnlyInSource.length} to create</Badge>
            <Badge size="sm" color="yellow">{result.changedTables.length} changed</Badge>
            <Badge size="sm" color="red">{result.tablesOnlyInTarget.length} to drop</Badge>
            <Badge size="sm" color="gray">{result.identicalTables.length} identical</Badge>
          </Group>

          <ScrollArea h={200}>
            <List size="xs" spacing={2}>
              {result.tablesOnlyInSource.map(t =>
                <List.Item key={`s${t}`}><Text span c="green" size="xs">+ {t}</Text></List.Item>)}
              {result.changedTables.map(t => (
                <List.Item key={`c${t.name}`}>
                  <Text span c="yellow" size="xs">~ {t.name}</Text>
                  <Text span size="xs" c="dimmed">
                    {" "}{[...t.addedColumns.map(c => `+${c}`), ...t.removedColumns.map(c => `-${c}`),
                      ...t.changedColumns.map(c => `~${c}`)].join(" ")}
                  </Text>
                </List.Item>
              ))}
              {result.tablesOnlyInTarget.map(t =>
                <List.Item key={`t${t}`}><Text span c="red" size="xs">- {t}</Text></List.Item>)}
            </List>
          </ScrollArea>

          {/* The script is shown, never run from here: applying it belongs in a query tab. */}
          <Text size="xs" c="dimmed">Sync script (copy it into a query tab to run it):</Text>
          <ScrollArea h={200}>
            <Code block fz="xs">{result.script || "-- the schemas already match"}</Code>
          </ScrollArea>
        </Stack>
      ) : null}
    </Stack>
  );
}

function DataCompare({ initialConnectionId }: { initialConnectionId: string }) {
  const connections = useConnections();
  const [source, setSource] = useState<string | null>(initialConnectionId || null);
  const [target, setTarget] = useState<string | null>(null);
  const [sourceRef, setSourceRef] = useState("");
  const [targetRef, setTargetRef] = useState("");
  const [keys, setKeys] = useState("");
  const [result, setResult] = useState<DataComparisonDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = () => {
    if (!source || !target) return;
    setBusy(true);
    setError(null);
    compareData({
      sourceConnectionId: source, sourceRef,
      targetConnectionId: target, targetRef: targetRef || sourceRef,
      keyColumns: keys.split(",").map(k => k.trim()).filter(Boolean),
    }).then(setResult).catch(e => setError(e.message)).finally(() => setBusy(false));
  };

  return (
    <Stack gap="xs" p="xs">
      <Group gap={6} align="flex-end">
        <Select size="xs" label="Source" data={options(connections)} value={source} onChange={setSource} />
        <TextInput size="xs" label="Source table" placeholder="Table:public/people" value={sourceRef}
          onChange={e => setSourceRef(e.currentTarget.value)} />
        <Select size="xs" label="Target" data={options(connections)} value={target} onChange={setTarget} />
        <TextInput size="xs" label="Target table" placeholder="same as source" value={targetRef}
          onChange={e => setTargetRef(e.currentTarget.value)} />
        <TextInput size="xs" label="Key columns" placeholder="primary key" value={keys}
          onChange={e => setKeys(e.currentTarget.value)} />
        <Button size="compact-xs" disabled={!source || !target || !sourceRef} loading={busy}
          onClick={run}>Compare</Button>
      </Group>

      {error ? <Alert color="red" variant="light">{error}</Alert> : null}

      {result ? (
        <Stack gap="xs">
          <Group gap={6}>
            <Badge size="sm" color="green">{result.missing.length} missing in target</Badge>
            <Badge size="sm" color="yellow">{result.different.length} different</Badge>
            <Badge size="sm" color="red">{result.extra.length} extra in target</Badge>
            <Badge size="sm" color="gray">{result.identical} identical</Badge>
            {result.truncated ? <Badge size="sm" color="orange">truncated</Badge> : null}
          </Group>

          <ScrollArea h={200}>
            <Table striped fz="xs" stickyHeader>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Key</Table.Th><Table.Th>Changed columns</Table.Th>
                  <Table.Th>Source</Table.Th><Table.Th>Target</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {result.different.map((row, index) => (
                  <Table.Tr key={index}>
                    <Table.Td>{row.key.map(String).join(", ")}</Table.Td>
                    <Table.Td>{row.changedColumns.join(", ")}</Table.Td>
                    <Table.Td>{row.sourceRow.map(String).join(" | ")}</Table.Td>
                    <Table.Td>{row.targetRow.map(String).join(" | ")}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </ScrollArea>

          <ScrollArea h={180}>
            <Code block fz="xs">{result.script || "-- the data already matches"}</Code>
          </ScrollArea>
        </Stack>
      ) : null}
    </Stack>
  );
}

export function ComparePanel({ connectionId = "" }: { connectionId?: string }) {
  return (
    <Tabs defaultValue="schema" keepMounted={false}>
      <Tabs.List>
        <Tabs.Tab value="schema">Schema</Tabs.Tab>
        <Tabs.Tab value="data">Data</Tabs.Tab>
      </Tabs.List>
      <Tabs.Panel value="schema"><SchemaCompare initialConnectionId={connectionId} /></Tabs.Panel>
      <Tabs.Panel value="data"><DataCompare initialConnectionId={connectionId} /></Tabs.Panel>
    </Tabs>
  );
}
