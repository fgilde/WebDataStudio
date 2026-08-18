import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Badge, Button, Group, Loader, Modal, MultiSelect, ScrollArea, Select, Stack, Switch,
  Table, Tabs, Text, TextInput, Tooltip,
} from "@mantine/core";
import {
  IconArrowDown, IconArrowUp, IconDeviceFloppy, IconPlus, IconTrash,
} from "@tabler/icons-react";
import {
  addColumn, emptyDefinition, moveColumn, NEUTRAL_TYPES, removeColumn, renameColumn, updateColumn,
  type ConstraintDefinition, type IndexDefinition, type TableDefinition,
} from "./definition";
import { applyDdl, loadDdl, previewDdl, type DdlPreviewDto } from "../api";

export function TableDesigner({ connectionId, objectRef, schema, onSaved }: {
  connectionId: string;
  /// Absent for a brand new table.
  objectRef?: string;
  schema: string;
  onSaved?: () => void;
}) {
  const [definition, setDefinition] = useState<TableDefinition | null>(null);
  const [original, setOriginal] = useState<string | null>(null);
  const [preview, setPreview] = useState<DdlPreviewDto | null>(null);
  const [confirmation, setConfirmation] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!objectRef) { setDefinition(emptyDefinition(schema)); return; }

    let cancelled = false;
    loadDdl(connectionId, objectRef)
      .then(d => {
        if (cancelled) return;
        setDefinition(d.definition);
        setOriginal(d.create ?? null);
      })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [connectionId, objectRef, schema]);

  if (error && !definition) return <Text c="red" size="xs" p="xs">{error}</Text>;
  if (!definition) return <Loader size="xs" m="xs" />;

  const patch = (next: TableDefinition) => setDefinition(next);

  const save = async () => {
    setBusy(true);
    setError(null);
    setConfirmation("");
    try { setPreview(await previewDdl(connectionId, objectRef ?? null, definition)); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  const apply = async () => {
    if (!preview) return;
    setBusy(true);
    try {
      await applyDdl(connectionId, preview.hash);
      setPreview(null);
      onSaved?.();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const confirmed = !preview?.destructive || confirmation.trim() === definition.name;

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4}>
        <TextInput size="xs" w={220} label={undefined} value={definition.name}
          onChange={e => patch({ ...definition, name: e.currentTarget.value })} />
        <Button size="compact-xs" leftSection={<IconDeviceFloppy size={13} />} loading={busy} onClick={save}>
          Save
        </Button>
        {error && <Text size="xs" c="red">{error}</Text>}
      </Group>

      <Tabs defaultValue="columns" style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column" }}>
        <Tabs.List>
          <Tabs.Tab value="columns">Columns</Tabs.Tab>
          <Tabs.Tab value="indexes">Indexes</Tabs.Tab>
          <Tabs.Tab value="constraints">Constraints</Tabs.Tab>
          {original && <Tabs.Tab value="ddl">Current DDL</Tabs.Tab>}
        </Tabs.List>

        <Tabs.Panel value="columns" style={{ flex: 1, minHeight: 0 }}>
          <ScrollArea h="100%">
            <Table fz="xs" striped>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Name</Table.Th><Table.Th>Type</Table.Th><Table.Th>Null</Table.Th>
                  <Table.Th>Default</Table.Th><Table.Th>Identity</Table.Th><Table.Th>Comment</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {definition.columns.map((column, index) => (
                  <Table.Tr key={index}>
                    <Table.Td>
                      <TextInput size="xs" value={column.name}
                        onChange={e => patch(renameColumn(definition, index, e.currentTarget.value))} />
                      {column.renamedFrom && (
                        <Badge size="xs" variant="light" mt={2}>was {column.renamedFrom}</Badge>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Select size="xs" searchable allowDeselect={false} data={NEUTRAL_TYPES}
                        value={NEUTRAL_TYPES.includes(column.type.toLowerCase()) ? column.type.toLowerCase() : null}
                        placeholder={column.type}
                        onChange={v => v && patch(updateColumn(definition, index, { type: v }))} />
                    </Table.Td>
                    <Table.Td>
                      <Switch size="xs" checked={column.nullable}
                        onChange={e => patch(updateColumn(definition, index, { nullable: e.currentTarget.checked }))} />
                    </Table.Td>
                    <Table.Td>
                      <TextInput size="xs" value={column.default ?? ""}
                        onChange={e => patch(updateColumn(definition, index,
                          { default: e.currentTarget.value || null }))} />
                    </Table.Td>
                    <Table.Td>
                      <Switch size="xs" checked={column.identity}
                        onChange={e => patch(updateColumn(definition, index, { identity: e.currentTarget.checked }))} />
                    </Table.Td>
                    <Table.Td>
                      <TextInput size="xs" value={column.comment ?? ""}
                        onChange={e => patch(updateColumn(definition, index,
                          { comment: e.currentTarget.value || null }))} />
                    </Table.Td>
                    <Table.Td>
                      <Group gap={2} wrap="nowrap">
                        <ActionIcon size="xs" variant="subtle" aria-label="Move up"
                          onClick={() => patch(moveColumn(definition, index, index - 1))}>
                          <IconArrowUp size={12} />
                        </ActionIcon>
                        <ActionIcon size="xs" variant="subtle" aria-label="Move down"
                          onClick={() => patch(moveColumn(definition, index, index + 1))}>
                          <IconArrowDown size={12} />
                        </ActionIcon>
                        <ActionIcon size="xs" variant="subtle" color="red" aria-label="Remove column"
                          onClick={() => patch(removeColumn(definition, index))}>
                          <IconTrash size={12} />
                        </ActionIcon>
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
            <Button size="compact-xs" variant="subtle" m={6} leftSection={<IconPlus size={12} />}
              onClick={() => patch(addColumn(definition))}>
              Add column
            </Button>
          </ScrollArea>
        </Tabs.Panel>

        <Tabs.Panel value="indexes" style={{ flex: 1, minHeight: 0 }}>
          <IndexEditor definition={definition} onChange={patch} />
        </Tabs.Panel>

        <Tabs.Panel value="constraints" style={{ flex: 1, minHeight: 0 }}>
          <ConstraintEditor definition={definition} onChange={patch} />
        </Tabs.Panel>

        {original && (
          <Tabs.Panel value="ddl" style={{ flex: 1, minHeight: 0 }}>
            <ScrollArea h="100%">
              <Text size="xs" ff="monospace" p={6} style={{ whiteSpace: "pre-wrap" }}>{original}</Text>
            </ScrollArea>
          </Tabs.Panel>
        )}
      </Tabs>

      <Modal opened={preview !== null} onClose={() => setPreview(null)} title="Migration preview" size="lg">
        {preview && (
          <Stack gap="sm">
            <Group gap={6}>
              <Badge size="xs" variant="light">{preview.statements.length} statements</Badge>
              {preview.destructive && <Badge size="xs" color="red" variant="light">destructive</Badge>}
              {!preview.transactional && (
                <Badge size="xs" color="orange" variant="light">no DDL rollback on this engine</Badge>
              )}
            </Group>

            <ScrollArea h={260}>
              <Stack gap={4}>
                {preview.statements.map((s, i) => (
                  <div key={i}>
                    <Text size="10px" c={s.destructive ? "red" : "dimmed"}>{s.description}</Text>
                    <Text size="xs" ff="monospace" style={{ whiteSpace: "pre-wrap" }}>{s.sql}</Text>
                  </div>
                ))}
              </Stack>
            </ScrollArea>

            {preview.destructive && (
              <Alert color="red" p="xs">
                <Text size="sm">This script drops data. Type <b>{definition.name}</b> to confirm.</Text>
                <TextInput mt={6} size="xs" value={confirmation}
                  onChange={e => setConfirmation(e.currentTarget.value)} />
              </Alert>
            )}

            <Group justify="flex-end">
              <Button variant="subtle" onClick={() => setPreview(null)}>Cancel</Button>
              <Button onClick={apply} loading={busy} disabled={!confirmed}>Apply</Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </div>
  );
}

function IndexEditor({ definition, onChange }: {
  definition: TableDefinition;
  onChange: (next: TableDefinition) => void;
}) {
  const set = (indexes: IndexDefinition[]) => onChange({ ...definition, indexes });

  return (
    <ScrollArea h="100%">
      <Table fz="xs">
        <Table.Thead>
          <Table.Tr><Table.Th>Name</Table.Th><Table.Th>Columns</Table.Th>
            <Table.Th>Unique</Table.Th><Table.Th>Filter</Table.Th><Table.Th /></Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {definition.indexes.map((index, i) => (
            <Table.Tr key={i}>
              <Table.Td>
                <TextInput size="xs" value={index.name}
                  onChange={e => set(definition.indexes.map((x, j) =>
                    j === i ? { ...x, name: e.currentTarget.value } : x))} />
              </Table.Td>
              <Table.Td>
                <MultiSelect size="xs" searchable data={definition.columns.map(c => c.name)}
                  value={index.columns}
                  onChange={v => set(definition.indexes.map((x, j) =>
                    j === i ? { ...x, columns: v } : x))} />
              </Table.Td>
              <Table.Td>
                <Switch size="xs" checked={index.unique}
                  onChange={e => set(definition.indexes.map((x, j) =>
                    j === i ? { ...x, unique: e.currentTarget.checked } : x))} />
              </Table.Td>
              <Table.Td>
                <TextInput size="xs" value={index.filter ?? ""} placeholder="partial index predicate"
                  onChange={e => set(definition.indexes.map((x, j) =>
                    j === i ? { ...x, filter: e.currentTarget.value || null } : x))} />
              </Table.Td>
              <Table.Td>
                <ActionIcon size="xs" variant="subtle" color="red" aria-label="Remove index"
                  onClick={() => set(definition.indexes.filter((_, j) => j !== i))}>
                  <IconTrash size={12} />
                </ActionIcon>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      <Button size="compact-xs" variant="subtle" m={6} leftSection={<IconPlus size={12} />}
        onClick={() => set([...definition.indexes, {
          name: `ix_${definition.name}_${definition.indexes.length + 1}`, columns: [], unique: false,
        }])}>
        Add index
      </Button>
    </ScrollArea>
  );
}

function ConstraintEditor({ definition, onChange }: {
  definition: TableDefinition;
  onChange: (next: TableDefinition) => void;
}) {
  const set = (constraints: ConstraintDefinition[]) => onChange({ ...definition, constraints });

  return (
    <ScrollArea h="100%">
      <Table fz="xs">
        <Table.Thead>
          <Table.Tr><Table.Th>Name</Table.Th><Table.Th>Kind</Table.Th><Table.Th>Columns</Table.Th>
            <Table.Th>Expression / target</Table.Th><Table.Th /></Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {definition.constraints.map((constraint, i) => (
            <Table.Tr key={i}>
              <Table.Td>
                <TextInput size="xs" value={constraint.name}
                  onChange={e => set(definition.constraints.map((x, j) =>
                    j === i ? { ...x, name: e.currentTarget.value } : x))} />
              </Table.Td>
              <Table.Td>
                <Select size="xs" allowDeselect={false}
                  data={["PrimaryKey", "Unique", "Check", "ForeignKey"]}
                  value={constraint.kind}
                  onChange={v => v && set(definition.constraints.map((x, j) =>
                    j === i ? { ...x, kind: v as ConstraintDefinition["kind"] } : x))} />
              </Table.Td>
              <Table.Td>
                <MultiSelect size="xs" searchable data={definition.columns.map(c => c.name)}
                  value={constraint.columns}
                  onChange={v => set(definition.constraints.map((x, j) =>
                    j === i ? { ...x, columns: v } : x))} />
              </Table.Td>
              <Table.Td>
                {constraint.kind === "Check" ? (
                  <TextInput size="xs" value={constraint.expression ?? ""} placeholder="amount > 0"
                    onChange={e => set(definition.constraints.map((x, j) =>
                      j === i ? { ...x, expression: e.currentTarget.value } : x))} />
                ) : constraint.kind === "ForeignKey" ? (
                  <Group gap={4} wrap="nowrap">
                    <TextInput size="xs" placeholder="table" value={constraint.referencedTable ?? ""}
                      onChange={e => set(definition.constraints.map((x, j) =>
                        j === i ? { ...x, referencedTable: e.currentTarget.value } : x))} />
                    <TextInput size="xs" placeholder="column" value={(constraint.referencedColumns ?? []).join(",")}
                      onChange={e => set(definition.constraints.map((x, j) =>
                        j === i ? { ...x, referencedColumns: e.currentTarget.value.split(",").map(s => s.trim()) } : x))} />
                  </Group>
                ) : null}
              </Table.Td>
              <Table.Td>
                <Tooltip label="Remove constraint">
                  <ActionIcon size="xs" variant="subtle" color="red" aria-label="Remove constraint"
                    onClick={() => set(definition.constraints.filter((_, j) => j !== i))}>
                    <IconTrash size={12} />
                  </ActionIcon>
                </Tooltip>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      <Button size="compact-xs" variant="subtle" m={6} leftSection={<IconPlus size={12} />}
        onClick={() => set([...definition.constraints, {
          name: `ck_${definition.name}_${definition.constraints.length + 1}`,
          kind: "Check", columns: [], expression: "",
        }])}>
        Add constraint
      </Button>
    </ScrollArea>
  );
}
