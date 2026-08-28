import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Code, FileInput, Group, Modal, ScrollArea, Select, Stack, Table, Text,
  TextInput,
} from "@mantine/core";
import { importFileAsTable, listConnections, type Connection, type ImportPlanDto } from "../api";

/// A file becomes a table.
///
/// The other import fills a table that already exists, with the columns mapped by hand. This is for
/// the CSV somebody was sent: the studio reads it, says what the table will look like, and creates it
/// only after that has been read.
export function NewTableDialog({ connectionId, source, onClose, onDone }: {
  /// Where the table is created. Empty when the dialog was opened from a bucket and the target is
  /// still to be chosen.
  connectionId: string;
  /// An uploaded file, or an object in a bucket that is read where it is.
  source?: { storageConnection: string; objectRef: string; name: string };
  onClose: () => void;
  onDone?: (table: string) => void;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [table, setTable] = useState(source ? suggest(source.name) : "");
  const [schema, setSchema] = useState("");
  const [plan, setPlan] = useState<ImportPlanDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [target, setTarget] = useState(connectionId);
  const [targets, setTargets] = useState<Connection[]>([]);

  // Opened from a bucket, the file and the target are two different connections, so the target is
  // asked for. Object storage is not one of the answers: a bucket has no table to create.
  useEffect(() => {
    if (!source) return;

    listConnections()
      .then(all => {
        const usable = all.filter(connection => connection.engine !== "storage"
          && connection.engine !== "redis" && connection.engine !== "mongodb");
        setTargets(usable);
        setTarget(current => current || usable[0]?.id || "");
      })
      .catch(e => setError(e.message));
  }, [source]);

  const ready = target.length > 0 && table.trim().length > 0
    && (file !== null || source !== undefined);

  const run = (apply: boolean) => {
    setBusy(true);
    setError(null);
    importFileAsTable(target, {
      table: table.trim(), schema: schema.trim() || undefined, apply, file, source,
    })
      .then(result => {
        if (apply) {
          onDone?.(("table" in result ? result.table : table) as string);
          onClose();
          return;
        }

        setPlan(result as ImportPlanDto);
      })
      .catch(e => setError(e.message))
      .finally(() => setBusy(false));
  };

  return (
    <Modal opened onClose={onClose} size="lg" title="A file as a new table">
      <Stack gap="sm">
        {source
          ? <>
              <Text size="sm">
                Reading <b>{source.name}</b> where it is, in the bucket — no download.
              </Text>
              <Select size="xs" label="Into this connection" searchable
                      data={targets.map(connection => ({
                        value: connection.id,
                        label: `${connection.name} · ${connection.engine}`,
                      }))}
                      value={target || null}
                      onChange={value => { setTarget(value ?? ""); setPlan(null); }} />
            </>
          : <FileInput size="xs" label="File" placeholder="CSV, TSV, JSON or Parquet"
                       value={file}
                       onChange={value => {
                         setFile(value);
                         setPlan(null);
                         if (value && !table.trim()) setTable(suggest(value.name));
                       }} />}

        <Group grow>
          <TextInput size="xs" label="New table" placeholder="people" value={table}
                     onChange={event => { setTable(event.currentTarget.value); setPlan(null); }} />
          <TextInput size="xs" label="Schema" placeholder="the engine's default" value={schema}
                     onChange={event => { setSchema(event.currentTarget.value); setPlan(null); }} />
        </Group>

        {error && <Alert color="red" variant="light">{error}</Alert>}

        {plan && (
          <>
            <Group gap="xs">
              <Text size="xs" fw={600}>{plan.columns.length} columns</Text>
              {plan.rows !== null && <Badge size="xs" variant="light">{plan.rows} rows</Badge>}
            </Group>

            <ScrollArea h={190}>
              <Table striped fz="xs" stickyHeader>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Column</Table.Th><Table.Th>In the file</Table.Th>
                    <Table.Th>In the table</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {plan.columns.map(column => (
                    <Table.Tr key={column.name}>
                      <Table.Td>{column.name}</Table.Td>
                      <Table.Td><Text size="xs" c="dimmed">{column.sourceType}</Text></Table.Td>
                      <Table.Td>{column.targetType}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea>

            <Stack gap={2}>
              <Text size="xs" fw={600}>What will run</Text>
              <Code block fz="xs" style={{ maxHeight: 130, overflow: "auto" }}>{plan.createSql}</Code>
            </Stack>
          </>
        )}

        <Group justify="space-between">
          <Button variant="default" onClick={() => run(false)} loading={busy} disabled={!ready}>
            {plan ? "Read it again" : "Read the file"}
          </Button>
          <Group gap="xs">
            <Button variant="subtle" onClick={onClose}>Cancel</Button>
            {/* Nothing is created until the plan above has been shown. */}
            <Button onClick={() => run(true)} loading={busy} disabled={!ready || plan === null}>
              Create and load
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}

/// `people-2026.csv` becomes `people_2026`: a name somebody does not have to invent.
function suggest(fileName: string): string {
  const base = fileName.replace(/\.[^.]+$/, "").split(/[\\/]/).pop() ?? "";
  return base.replace(/[^A-Za-z0-9]+/g, "_").replace(/^_+|_+$/g, "").toLowerCase();
}
