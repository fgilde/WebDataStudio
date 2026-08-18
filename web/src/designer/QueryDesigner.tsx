import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ActionIcon, Button, Checkbox, Group, NumberInput, ScrollArea, Select, Stack, Table, Text,
  TextInput, Tooltip,
} from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { listSchema, describeObject, type SchemaNodeDto } from "../api";
import { buildSelect, emptyModel, type JoinKind, type QueryModel } from "./buildSelect";
import type { DialectId } from "../sql/splitStatements";

const OPERATORS = ["=", "<>", ">", ">=", "<", "<=", "LIKE", "IN", "IS NULL", "IS NOT NULL"];

interface TableColumns { alias: string; ref: string; columns: string[] }

/// Builds a SELECT visually and hands it to a query tab. One direction only: parsing arbitrary
/// SQL back into this model is a different project.
export function QueryDesigner({ connectionId, dialect, onOpenInTab }: {
  connectionId: string;
  dialect: DialectId;
  onOpenInTab: (sql: string) => void;
}) {
  const [available, setAvailable] = useState<SchemaNodeDto[]>([]);
  const [loaded, setLoaded] = useState<TableColumns[]>([]);
  const [model, setModel] = useState<QueryModel>(emptyModel);

  useEffect(() => {
    // One flat list of tables to pick from; the explorer's tree is overkill inside a form.
    let cancelled = false;

    (async () => {
      const roots = await listSchema(connectionId).catch(() => []);
      const found: SchemaNodeDto[] = [];

      for (const root of roots) {
        if (!root.hasChildren) continue;
        const children = await listSchema(connectionId, root.ref).catch(() => []);

        for (const child of children) {
          if (child.kind === "Table") found.push(child);
          else if (child.hasChildren && child.kind === "TableFolder")
            found.push(...(await listSchema(connectionId, child.ref).catch(() => []))
              .filter((n: SchemaNodeDto) => n.kind === "Table"));
        }
      }

      if (!cancelled) setAvailable(found);
    })();

    return () => { cancelled = true; };
  }, [connectionId]);

  const addTable = useCallback(async (ref: string) => {
    const node = available.find(n => n.ref === ref);
    if (!node) return;

    const detail = await describeObject(connectionId, ref).catch(() => null);
    const alias = String.fromCharCode(97 + loaded.length);
    const parts = ref.split(":", 2)[1]?.split("/") ?? [];

    setLoaded(list => [...list, { alias, ref, columns: detail?.columns.map(c => c.name) ?? [] }]);
    setModel(m => ({
      ...m,
      tables: [...m.tables, {
        name: parts[parts.length - 1],
        schema: parts.length > 1 ? parts[0] : undefined,
        alias,
      }],
    }));
  }, [available, connectionId, loaded.length]);

  const sql = useMemo(() => buildSelect(model, dialect), [model, dialect]);

  const toggleColumn = (alias: string, column: string) =>
    setModel(m => ({
      ...m,
      columns: m.columns.some(c => c.table === alias && c.column === column)
        ? m.columns.filter(c => !(c.table === alias && c.column === column))
        : [...m.columns, { table: alias, column }],
    }));

  return (
    <div style={{ display: "flex", height: "100%", minHeight: 0 }}>
      <div style={{ width: 300, borderRight: "1px solid var(--mantine-color-default-border)" }}>
        <Stack gap={4} p={4}>
          <Select size="xs" searchable placeholder="Add a table" value={null}
            data={available.map(n => ({ value: n.ref, label: n.label }))}
            onChange={ref => ref && addTable(ref)} />
        </Stack>

        <ScrollArea h="calc(100% - 44px)">
          {loaded.map(table => (
            <div key={table.alias} style={{ padding: 6 }}>
              <Group gap={4}>
                <Text size="xs" fw={600}>{table.alias}</Text>
                <Text size="xs" c="dimmed">{table.ref.split("/").pop()}</Text>
              </Group>
              {table.columns.map(column => (
                <Checkbox key={column} size="xs" ml={10} label={column}
                  checked={model.columns.some(c => c.table === table.alias && c.column === column)}
                  onChange={() => toggleColumn(table.alias, column)} />
              ))}
            </div>
          ))}
        </ScrollArea>
      </div>

      <div style={{ flex: 1, minWidth: 0, display: "flex", flexDirection: "column" }}>
        <ScrollArea style={{ flex: 1, minHeight: 0 }} p={6}>
          <Stack gap="sm">
            <Section title="Joins" onAdd={loaded.length < 2 ? undefined : () => setModel(m => ({
              ...m,
              joins: [...m.joins, {
                left: loaded[0].alias, right: loaded[1].alias,
                leftColumn: loaded[0].columns[0] ?? "", rightColumn: loaded[1].columns[0] ?? "",
                kind: "INNER",
              }],
            }))}>
              {model.joins.map((join, index) => (
                <Group key={index} gap={4} wrap="nowrap">
                  <Select size="xs" w={60} data={loaded.map(t => t.alias)} value={join.left}
                    onChange={v => patchJoin(index, { left: v ?? join.left })} />
                  <Select size="xs" w={130} data={columnsOf(loaded, join.left)} value={join.leftColumn}
                    onChange={v => patchJoin(index, { leftColumn: v ?? join.leftColumn })} />
                  <Select size="xs" w={90} data={["INNER", "LEFT", "RIGHT", "FULL"]} value={join.kind}
                    onChange={v => patchJoin(index, { kind: (v ?? "INNER") as JoinKind })} />
                  <Select size="xs" w={60} data={loaded.map(t => t.alias)} value={join.right}
                    onChange={v => patchJoin(index, { right: v ?? join.right })} />
                  <Select size="xs" w={130} data={columnsOf(loaded, join.right)} value={join.rightColumn}
                    onChange={v => patchJoin(index, { rightColumn: v ?? join.rightColumn })} />
                  <ActionIcon size="sm" variant="subtle" color="red" aria-label="Remove join"
                    onClick={() => setModel(m => ({ ...m, joins: m.joins.filter((_, i) => i !== index) }))}>
                    <IconTrash size={13} />
                  </ActionIcon>
                </Group>
              ))}
            </Section>

            <Section title="Filters" onAdd={loaded.length === 0 ? undefined : () => setModel(m => ({
              ...m,
              filters: [...m.filters, {
                table: loaded[0].alias, column: loaded[0].columns[0] ?? "", operator: "=", value: "",
              }],
            }))}>
              {model.filters.map((filter, index) => (
                <Group key={index} gap={4} wrap="nowrap">
                  <Select size="xs" w={60} data={loaded.map(t => t.alias)} value={filter.table}
                    onChange={v => patchFilter(index, { table: v ?? filter.table })} />
                  <Select size="xs" w={150} data={columnsOf(loaded, filter.table)} value={filter.column}
                    onChange={v => patchFilter(index, { column: v ?? filter.column })} />
                  <Select size="xs" w={110} data={OPERATORS} value={filter.operator}
                    onChange={v => patchFilter(index, { operator: v ?? "=" })} />
                  <TextInput size="xs" w={160} value={filter.value}
                    disabled={filter.operator.startsWith("IS ")}
                    onChange={e => patchFilter(index, { value: e.currentTarget.value })} />
                  <ActionIcon size="sm" variant="subtle" color="red" aria-label="Remove filter"
                    onClick={() => setModel(m => ({ ...m, filters: m.filters.filter((_, i) => i !== index) }))}>
                    <IconTrash size={13} />
                  </ActionIcon>
                </Group>
              ))}
            </Section>

            <Group gap="md">
              <Checkbox size="xs" label="Group by the plain columns" checked={model.grouping}
                onChange={e => setModel(m => ({ ...m, grouping: e.currentTarget.checked }))} />
              <NumberInput size="xs" w={120} label="Limit" min={0} value={model.limit ?? 0}
                onChange={v => setModel(m => ({ ...m, limit: Number(v) || undefined }))} />
            </Group>

            <Section title="Order">
              <Table fz="xs">
                <Table.Tbody>
                  {model.columns.map(column => (
                    <Table.Tr key={`${column.table}.${column.column}`}>
                      <Table.Td>{column.table}.{column.column}</Table.Td>
                      <Table.Td w={130}>
                        <Select size="xs" data={["none", "ascending", "descending"]}
                          value={orderOf(model, column.table, column.column)}
                          onChange={v => setOrder(column.table, column.column, v ?? "none")} />
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Section>
          </Stack>
        </ScrollArea>

        <div style={{ borderTop: "1px solid var(--mantine-color-default-border)", padding: 6 }}>
          <Group justify="space-between" mb={4}>
            <Text size="xs" c="dimmed">Generated SQL</Text>
            <Tooltip label="Hand the statement to a query tab for editing">
              <Button size="compact-xs" disabled={!sql} onClick={() => onOpenInTab(sql)}>
                Open in query tab
              </Button>
            </Tooltip>
          </Group>
          <pre style={{ margin: 0, fontSize: 11, whiteSpace: "pre-wrap" }}>
            {sql || "-- pick a table and at least one column"}
          </pre>
        </div>
      </div>
    </div>
  );

  function patchJoin(index: number, patch: Partial<QueryModel["joins"][number]>) {
    setModel(m => ({ ...m, joins: m.joins.map((j, i) => (i === index ? { ...j, ...patch } : j)) }));
  }

  function patchFilter(index: number, patch: Partial<QueryModel["filters"][number]>) {
    setModel(m => ({ ...m, filters: m.filters.map((f, i) => (i === index ? { ...f, ...patch } : f)) }));
  }

  function setOrder(table: string, column: string, mode: string) {
    setModel(m => ({
      ...m,
      order: mode === "none"
        ? m.order.filter(o => !(o.table === table && o.column === column))
        : [
          ...m.order.filter(o => !(o.table === table && o.column === column)),
          { table, column, descending: mode === "descending" },
        ],
    }));
  }
}

const columnsOf = (loaded: TableColumns[], alias: string) =>
  loaded.find(t => t.alias === alias)?.columns ?? [];

const orderOf = (model: QueryModel, table: string, column: string) => {
  const entry = model.order.find(o => o.table === table && o.column === column);
  return entry ? (entry.descending ? "descending" : "ascending") : "none";
};

function Section({ title, onAdd, children }: {
  title: string; onAdd?: () => void; children: React.ReactNode;
}) {
  return (
    <div>
      <Group gap={4} mb={4}>
        <Text size="xs" fw={600}>{title}</Text>
        {onAdd ? (
          <ActionIcon size="xs" variant="subtle" aria-label={`Add ${title}`} onClick={onAdd}>
            <IconPlus size={13} />
          </ActionIcon>
        ) : null}
      </Group>
      <Stack gap={4}>{children}</Stack>
    </div>
  );
}
