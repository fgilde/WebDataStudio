import { useEffect, useState } from "react";
import {
  Alert, Badge, Button, Group, Modal, NumberInput, ScrollArea, Select, Stack, Table, Text,
} from "@mantine/core";
import {
  applyChanges, generateStrategies, previewGenerate,
  type ChangePreviewDto, type GenerateStrategiesDto,
} from "../api";

/// Fills an empty table with plausible rows. The generated inserts go through the same preview and
/// apply as a hand edit — nothing reaches the database that was not shown as SQL first.
export function GenerateDialog({ connectionId, objectRef, tableName, opened, onClose, onApplied }: {
  connectionId: string;
  objectRef: string;
  tableName: string;
  opened: boolean;
  onClose: () => void;
  onApplied: () => void;
}) {
  const [columns, setColumns] = useState<GenerateStrategiesDto | null>(null);
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const [rows, setRows] = useState<number | string>(50);
  const [seed, setSeed] = useState<number | string>(1);
  const [preview, setPreview] = useState<(ChangePreviewDto & { emptyForeignKeys?: string[] }) | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!opened) return;
    setPreview(null);
    setError(null);
    generateStrategies(connectionId, objectRef)
      .then(state => {
        setColumns(state);
        setChosen(Object.fromEntries(state.columns.map(c => [c.name, c.strategy])));
      })
      .catch(e => setError(e instanceof Error ? e.message : String(e)));
  }, [opened, connectionId, objectRef]);

  const build = async () => {
    setError(null);
    setBusy(true);
    try {
      setPreview(await previewGenerate(connectionId, objectRef, {
        rows: typeof rows === "number" ? rows : 50,
        seed: typeof seed === "number" ? seed : undefined,
        strategies: chosen,
      }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const apply = async () => {
    if (!preview) return;
    setBusy(true);
    try {
      await applyChanges(connectionId, objectRef, preview.hash);
      onApplied();
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={`Generate rows for ${tableName}`} size="lg">
      <Stack gap="sm">
        {error && <Alert color="red" p="xs"><Text size="sm">{error}</Text></Alert>}

        <Group gap="sm">
          <NumberInput size="xs" w={120} label="Rows" min={1} max={10000} value={rows}
            onChange={setRows} />
          <NumberInput size="xs" w={120} label="Seed" min={0} value={seed} onChange={setSeed} />
          <Text size="xs" c="dimmed" mt={18}>The same seed gives the same rows.</Text>
        </Group>

        {columns && (
          <ScrollArea.Autosize mah={260}>
            <Table striped>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Column</Table.Th>
                  <Table.Th>Type</Table.Th>
                  <Table.Th>Filled with</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {columns.columns.map(column => (
                  <Table.Tr key={column.name}>
                    <Table.Td>
                      <Text size="xs">{column.name}</Text>
                      {!column.nullable && <Badge size="xs" variant="light" ml={4}>required</Badge>}
                    </Table.Td>
                    <Table.Td><Text size="xs" c="dimmed">{column.dataType}</Text></Table.Td>
                    <Table.Td>
                      <Select size="xs" data={columns.available} allowDeselect={false}
                        value={chosen[column.name] ?? column.strategy}
                        onChange={value => setChosen(c => ({ ...c, [column.name]: value ?? "auto" }))} />
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </ScrollArea.Autosize>
        )}

        {preview && (
          <>
            <Group gap="xs">
              <Badge variant="light">{preview.statementCount} inserts</Badge>
              {preview.emptyForeignKeys?.length
                ? (
                  <Badge color="orange" variant="light">
                    no parent rows for {preview.emptyForeignKeys.join(", ")}
                  </Badge>
                )
                : null}
            </Group>
            <ScrollArea h={180}>
              <Text size="xs" ff="monospace" style={{ whiteSpace: "pre-wrap" }}>{preview.script}</Text>
            </ScrollArea>
          </>
        )}

        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>Cancel</Button>
          <Button variant="default" loading={busy} onClick={build}>Preview</Button>
          <Button disabled={!preview} loading={busy} onClick={apply}>Insert</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
