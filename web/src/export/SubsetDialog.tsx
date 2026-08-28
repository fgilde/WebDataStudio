import { useState } from "react";
import {
  Alert, Badge, Button, Checkbox, Code, Group, Modal, NumberInput, ScrollArea, Stack, Table, Text,
  TextInput,
} from "@mantine/core";
import { buildSubset, type SubsetResultDto } from "../api";

/// A small, loadable, anonymised copy of a real database.
///
/// The usual answer to "I need production-like data" is a full dump, which is both too big to work
/// with and too dangerous to keep on a laptop. This takes a few hundred rows from one table, follows
/// the foreign keys upwards so the rows are actually loadable, replaces what is about people, and
/// hands back one SQL script — the same script WDS_SEED_SQL loads into a fresh container.
export function SubsetDialog({ target, onClose, onOpenInEditor }: {
  /// The connection and the table to start from, or null when the dialog is closed.
  target: { connectionId: string; schema: string; table: string } | null;
  onClose: () => void;
  onOpenInEditor?: (sql: string) => void;
}) {
  const [rows, setRows] = useState(200);
  const [where, setWhere] = useState("");
  const [depth, setDepth] = useState(4);
  const [anonymise, setAnonymise] = useState(true);
  const [includeSchema, setIncludeSchema] = useState(true);
  const [result, setResult] = useState<SubsetResultDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const build = () => {
    if (!target) return;

    setBusy(true);
    setError(null);
    setResult(null);
    buildSubset(target.connectionId, {
      table: target.table, schema: target.schema, where: where || undefined,
      rows, depth, anonymise, includeSchema,
    })
      .then(setResult)
      .catch(e => setError(e.message))
      .finally(() => setBusy(false));
  };

  const download = () => {
    if (!result || !target) return;

    const url = URL.createObjectURL(new Blob([result.script], { type: "application/sql" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = `${target.table}-subset.sql`;
    link.click();
    URL.revokeObjectURL(url);
  };

  return (
    <Modal opened={target !== null} onClose={onClose} size="xl"
      title={`Development subset of ${target?.table ?? ""}`}>
      <Stack gap="sm">
        <Text size="xs" c="dimmed">
          These rows, the rows they point at, and nothing else. What is about people is replaced;
          keys are not, so the script still loads.
        </Text>

        <Group gap="sm" align="flex-end">
          <NumberInput size="xs" w={110} label="Rows" min={1} max={5000} value={rows}
            onChange={value => setRows(Number(value) || 200)} />
          <NumberInput size="xs" w={130} label="Follow references" min={0} max={8} value={depth}
            onChange={value => setDepth(Number(value) || 0)} />
          <TextInput size="xs" flex={1} label="Where" placeholder="placed > '2026-01-01'"
            value={where} onChange={e => setWhere(e.currentTarget.value)} />
        </Group>

        <Group gap="md">
          <Checkbox size="xs" label="Replace what is about people" checked={anonymise}
            onChange={e => setAnonymise(e.currentTarget.checked)} />
          <Checkbox size="xs" label="CREATE TABLE first" checked={includeSchema}
            onChange={e => setIncludeSchema(e.currentTarget.checked)} />
          <Button size="compact-xs" loading={busy} onClick={build}>Build the subset</Button>
        </Group>

        {!anonymise && (
          <Alert color="yellow" variant="light">
            This will be real data. It belongs wherever real data belongs — not in a repository and
            not on a laptop.
          </Alert>
        )}

        {error && <Alert color="red" variant="light">{error}</Alert>}

        {result && (
          <>
            <Group gap="xs">
              <Badge size="sm" variant="light">{result.rows} rows</Badge>
              <Badge size="sm" variant="light">{result.tables.length} tables</Badge>
              {onOpenInEditor && (
                <Button size="compact-xs" variant="default"
                  onClick={() => { onOpenInEditor(result.script); onClose(); }}>
                  Open in editor
                </Button>
              )}
              <Button size="compact-xs" variant="default" onClick={download}>
                Download .sql
              </Button>
            </Group>

            {result.notes.map(note => (
              <Alert key={note} color="yellow" variant="light" p={6}>
                <Text size="xs">{note}</Text>
              </Alert>
            ))}

            <Table striped fz="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Table</Table.Th><Table.Th w={80}>Rows</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {result.tables.map(table => (
                  <Table.Tr key={`${table.schema}.${table.name}`}>
                    <Table.Td>
                      {table.schema ? `${table.schema}.${table.name}` : table.name}
                    </Table.Td>
                    <Table.Td>{table.rows}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>

            <ScrollArea h={220}>
              <Code block fz="xs">{result.script.slice(0, 20000)}</Code>
            </ScrollArea>
          </>
        )}
      </Stack>
    </Modal>
  );
}
