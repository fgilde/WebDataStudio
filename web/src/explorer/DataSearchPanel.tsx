import { useState } from "react";
import {
  Alert, Badge, Button, Group, Loader, ScrollArea, Stack, Switch, Table, Text, TextInput,
} from "@mantine/core";
import { searchData, type DataSearchDto } from "../api";

/// "Find 4711 in any table."
///
/// The object search says where a table is; this says where a value is. It runs on the server, one
/// query per table, and it is type-aware: a number is compared against numeric columns as a number,
/// and a column that could not hold the value is not scanned at all.
export function DataSearchPanel({ connectionId, schema, onOpen }: {
  connectionId: string;
  /// Which schema to search. Empty searches everything the connection can see.
  schema?: string;
  /// Opens a hit: the table, filtered on the column that matched.
  onOpen?: (table: string, schema: string, column: string, value: string) => void;
}) {
  const [value, setValue] = useState("");
  const [exact, setExact] = useState(false);
  const [result, setResult] = useState<DataSearchDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const search = () => {
    if (!value.trim()) return;

    setBusy(true);
    setError(null);
    searchData(connectionId, value, { schema, exact })
      .then(setResult)
      .catch(e => { setResult(null); setError(e.message); })
      .finally(() => setBusy(false));
  };

  return (
    <Stack gap={6} p="xs">
      <Group gap="xs" align="flex-end">
        <TextInput size="xs" style={{ flex: 1 }} label="Find this value" value={value}
                   placeholder="4711, an email address, part of a name"
                   onChange={event => setValue(event.currentTarget.value)}
                   onKeyDown={event => { if (event.key === "Enter") search(); }} />
        <Switch size="xs" label="whole value" checked={exact}
                onChange={event => setExact(event.currentTarget.checked)} />
        <Button size="compact-xs" onClick={search} loading={busy} disabled={!value.trim()}>
          Search
        </Button>
      </Group>

      {error && <Alert color="yellow" variant="light">{error}</Alert>}

      {result &&
        <Group gap="xs">
          <Text size="xs" c="dimmed">
            {result.hits.length} places · {result.tablesSearched} tables searched
            {result.tablesSkipped > 0 && `, ${result.tablesSkipped} skipped`}
          </Text>
          {result.truncated &&
            <Badge size="xs" color="orange">stopped at the table limit</Badge>}
        </Group>}

      {busy && <Loader size="xs" />}

      {result && result.hits.length === 0 && !busy &&
        <Text size="xs" c="dimmed">Not in any column that could hold it.</Text>}

      <ScrollArea h={300}>
        <Table striped highlightOnHover fz="xs" stickyHeader>
          <Table.Tbody>
            {result?.hits.map(hit => (
              <Table.Tr key={`${hit.schema}.${hit.table}.${hit.column}`}
                        style={{ cursor: onOpen ? "pointer" : undefined }}
                        onClick={() => onOpen?.(hit.table, hit.schema, hit.column, value)}>
                <Table.Td>{hit.schema ? `${hit.schema}.${hit.table}` : hit.table}</Table.Td>
                <Table.Td>{hit.column}</Table.Td>
                <Table.Td><Text size="xs" c="dimmed">{hit.dataType}</Text></Table.Td>
                <Table.Td>{hit.matches} rows</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>

      {result && result.notes.length > 0 &&
        <Stack gap={2}>
          <Text size="xs" fw={600}>Not searched</Text>
          {result.notes.slice(0, 8).map(note => (
            <Text key={note} size="xs" c="dimmed">{note}</Text>
          ))}
        </Stack>}
    </Stack>
  );
}
