import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  ActionIcon, Button, Checkbox, Group, NumberInput, ScrollArea, Select, Stack, Table, Text,
  TextInput, Tooltip,
} from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { listSchema, describeObject, type SchemaNodeDto } from "../api";
import { QueryCanvas } from "./QueryCanvas";
import { runQuery } from "../query/runQuery";
import { applyChunk, createResultState, type ResultState } from "../query/resultStore";
import { ResultArea } from "../query/ResultArea";
import {
  buildSelect, buildSelectWithModel, emptyModel, filterParameters, suggestJoin,
  type JoinKind, type LoadedTable, type QueryModel,
} from "./buildSelect";
import type { DialectId } from "../sql/splitStatements";

const OPERATORS = ["=", "<>", ">", ">=", "<", "<=", "LIKE", "IN", "IS NULL", "IS NOT NULL"];

/// Enough rows to see whether the query is the one you meant, few enough to run on every edit.
const PREVIEW_ROWS = 50;

/// A Select needs a value for "no aggregate", and an em dash reads as empty.
const NO_AGGREGATE = "\u2014";
const AGGREGATES = [NO_AGGREGATE, "count", "sum", "avg", "min", "max"];

/// A loaded table plus the reference it came from, which the model itself does not carry.
interface TableColumns extends LoadedTable { ref: string }

/// Builds a SELECT visually and hands it to a query tab. One direction only: parsing arbitrary
/// SQL back into this model is a different project.
export function QueryDesigner({ connectionId, dialect, onOpenInTab, initialModel }: {
  connectionId: string;
  dialect: DialectId;
  onOpenInTab: (sql: string) => void;
  /// A model recovered from a generated statement, so a query can come back into the builder.
  initialModel?: QueryModel;
}) {
  const [available, setAvailable] = useState<SchemaNodeDto[]>([]);
  const [loaded, setLoaded] = useState<TableColumns[]>([]);
  const [model, setModel] = useState<QueryModel>(initialModel ?? emptyModel);

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

  // A model that arrived from outside names its tables but carries no columns; load them once so
  // the canvas and the column lists have something to show.
  const restored = useRef(false);
  useEffect(() => {
    if (restored.current || !initialModel || initialModel.tables.length === 0) return;
    if (available.length === 0) return;
    restored.current = true;

    (async () => {
      const tables: TableColumns[] = [];

      for (const table of initialModel.tables) {
        const node = available.find(candidate => {
          const parts = candidate.ref.split(":", 2)[1]?.split("/") ?? [];
          return parts[parts.length - 1] === table.name
            && (!table.schema || parts.length < 2 || parts[0] === table.schema);
        });
        if (!node) continue;

        const detail = await describeObject(connectionId, node.ref).catch(() => null);
        tables.push({
          alias: table.alias, ref: node.ref, name: table.name, schema: table.schema,
          columns: detail?.columns.map(c => c.name) ?? [],
          foreignKeys: detail?.foreignKeys ?? [],
        });
      }

      setLoaded(tables);
    })();
  }, [available, connectionId, initialModel]);

  const addTable = useCallback(async (ref: string) => {
    const node = available.find(n => n.ref === ref);
    if (!node) return;

    const detail = await describeObject(connectionId, ref).catch(() => null);
    const alias = String.fromCharCode(97 + loaded.length);
    const parts = ref.split(":", 2)[1]?.split("/") ?? [];

    const table: TableColumns = {
      alias,
      ref,
      name: parts[parts.length - 1],
      schema: parts.length > 1 ? parts[0] : undefined,
      columns: detail?.columns.map(c => c.name) ?? [],
      foreignKeys: detail?.foreignKeys ?? [],
    };

    // The schema already knows how these tables relate; the first join that fits is proposed
    // rather than typed. Everything about it stays editable below.
    const joins = loaded
      .map(existing => suggestJoin(existing, table))
      .filter(join => join !== null);

    setLoaded(list => [...list, table]);
    setModel(m => ({
      ...m,
      tables: [...m.tables, { name: table.name, schema: table.schema, alias }],
      joins: joins.length > 0
        ? [...m.joins, {
            left: joins[0]!.left, leftColumn: joins[0]!.leftColumn,
            right: joins[0]!.right, rightColumn: joins[0]!.rightColumn,
            kind: joins[0]!.kind,
          }]
        : m.joins,
      // The remaining pairs of a composite key are conditions in their own right.
      filters: joins[0]?.extra
        ? [...m.filters, ...joins[0].extra.map(pair => ({
            table: joins[0]!.left, column: pair.leftColumn,
            operator: "=", value: `${joins[0]!.right}.${pair.rightColumn}`,
          }))]
        : m.filters,
    }));
  }, [available, connectionId, loaded]);

  const sql = useMemo(() => buildSelect(model, dialect), [model, dialect]);
  const [preview, setPreview] = useState<ResultState>(createResultState);

  // A sample of what the query returns, while it is still being built. Debounced, capped, and
  // never blocking: a query that does not run yet shows its error and the canvas keeps working.
  useEffect(() => {
    if (!sql) { setPreview(createResultState()); return; }

    let cancelled = false;
    const timer = window.setTimeout(() => {
      let next = createResultState();
      const run = runQuery(
        { connectionId, sql, maxRows: PREVIEW_ROWS, parameters: filterParameters(model) },
        chunk => {
          next = applyChunk(next, chunk);
          if (!cancelled) setPreview(next);
        });

      void run.done;
    }, 400);

    return () => { cancelled = true; window.clearTimeout(timer); };
  }, [sql, connectionId, model]);

  return (
    <div style={{ display: "flex", height: "100%", minHeight: 0 }}>
      <div style={{ width: 300, borderRight: "1px solid var(--mantine-color-default-border)" }}>
        <Stack gap={4} p={4}>
          <Select size="xs" searchable placeholder="Add a table" value={null}
            data={available.map(n => ({ value: n.ref, label: n.label }))}
            onChange={ref => ref && addTable(ref)} />
        </Stack>

        {/* The columns live on the canvas cards; this list is the inventory and how much of each
            table is selected, for a query with more tables than fit on screen. */}
        <ScrollArea h="calc(100% - 44px)">
          {loaded.map(table => (
            <div key={table.alias} style={{ padding: "4px 6px" }}>
              <Group gap={6} wrap="nowrap">
                <Text size="xs" fw={700}>{table.alias}</Text>
                <Text size="xs" truncate>{table.name}</Text>
                <Text size="10px" c="dimmed" ml="auto">
                  {model.columns.filter(c => c.table === table.alias).length}/{table.columns.length}
                </Text>
              </Group>
            </div>
          ))}
        </ScrollArea>
      </div>

      <div style={{ flex: 1, minWidth: 0, display: "flex", flexDirection: "column" }}>
        {/* The canvas is where the query is shaped: tables as cards, joins as lines, a checkbox per
            column. Everything below it stays as the exact form for what a line cannot express. */}
        <div style={{ height: "45%", minHeight: 180, borderBottom: "1px solid var(--mantine-color-default-border)" }}>
          {loaded.length === 0
            ? <Text size="xs" c="dimmed" p="sm">Add a table to start building.</Text>
            : <QueryCanvas model={model} loaded={loaded} onModelChange={setModel} />}
        </div>

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

            {/* HAVING only means anything once something aggregates, so the section appears then. */}
            {model.columns.some(c => c.aggregate) ? (
              <Section title="Having" onAdd={() => setModel(m => ({
                ...m,
                having: [...(m.having ?? []), {
                  table: loaded[0].alias, column: loaded[0].columns[0] ?? "",
                  operator: ">", value: "", aggregate: "sum",
                }],
              }))}>
                {(model.having ?? []).map((entry, index) => (
                  <Group key={index} gap={4} wrap="nowrap">
                    <Select size="xs" w={90} data={AGGREGATES.filter(a => a !== NO_AGGREGATE)}
                      value={entry.aggregate}
                      onChange={v => patchHaving(index, { aggregate: v ?? "sum" })} />
                    <Select size="xs" w={60} data={loaded.map(t => t.alias)} value={entry.table}
                      onChange={v => patchHaving(index, { table: v ?? entry.table })} />
                    <Select size="xs" w={130} data={columnsOf(loaded, entry.table)} value={entry.column}
                      onChange={v => patchHaving(index, { column: v ?? entry.column })} />
                    <Select size="xs" w={110} data={OPERATORS} value={entry.operator}
                      onChange={v => patchHaving(index, { operator: v ?? ">" })} />
                    <TextInput size="xs" w={140} value={entry.value}
                      disabled={entry.operator.startsWith("IS ")}
                      onChange={e => {
                        const value = e.currentTarget.value;
                        patchHaving(index, { value });
                      }} />
                    <ActionIcon size="sm" variant="subtle" color="red" aria-label="Remove having"
                      onClick={() => setModel(m => ({
                        ...m, having: (m.having ?? []).filter((_, i) => i !== index),
                      }))}>
                      <IconTrash size={13} />
                    </ActionIcon>
                  </Group>
                ))}
              </Section>
            ) : null}

            <Group gap="md">
              <Checkbox size="xs" label="Distinct" checked={model.distinct ?? false}
                onChange={e => {
                  const distinct = e.currentTarget.checked;
                  setModel(m => ({ ...m, distinct }));
                }} />
              <Checkbox size="xs" label="Group by the plain columns" checked={model.grouping}
                onChange={e => { const grouping = e.currentTarget.checked; setModel(m => ({ ...m, grouping })); }} />
              <NumberInput size="xs" w={120} label="Limit" min={0} value={model.limit ?? 0}
                onChange={v => setModel(m => ({ ...m, limit: Number(v) || undefined }))} />
            </Group>

            <Section title="Selected columns">
              <Table fz="xs">
                <Table.Tbody>
                  {model.columns.map((column, index) => (
                    <Table.Tr key={`${column.table}.${column.column}`}>
                      <Table.Td>{column.table}.{column.column}</Table.Td>
                      <Table.Td w={110}>
                        {/* An aggregate here is what turns the query into a grouped one. */}
                        <Select size="xs" data={AGGREGATES} value={column.aggregate ?? NO_AGGREGATE}
                          onChange={v => patchColumn(index, {
                            aggregate: !v || v === NO_AGGREGATE ? undefined : v,
                          })} />
                      </Table.Td>
                      <Table.Td w={130}>
                        <TextInput size="xs" placeholder="alias" value={column.alias ?? ""}
                          onChange={e => {
                            const alias = e.currentTarget.value;
                            patchColumn(index, { alias: alias || undefined });
                          }} />
                      </Table.Td>
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
              <Button size="compact-xs" disabled={!sql}
                onClick={() => onOpenInTab(buildSelectWithModel(model, dialect))}>
                Open in query tab
              </Button>
            </Tooltip>
          </Group>
          <pre style={{ margin: 0, fontSize: 11, whiteSpace: "pre-wrap", maxHeight: 120, overflow: "auto" }}>
            {sql || "-- pick a table and at least one column"}
          </pre>
        </div>

        {/* The first rows of the query being built. Same component as a query tab's result, so
            sorting, the cell viewer and the copy actions come along. */}
        <div style={{ height: 220, borderTop: "1px solid var(--mantine-color-default-border)" }}>
          <ResultArea result={preview} />
        </div>
      </div>
    </div>
  );

  function patchJoin(index: number, patch: Partial<QueryModel["joins"][number]>) {
    setModel(m => ({ ...m, joins: m.joins.map((j, i) => (i === index ? { ...j, ...patch } : j)) }));
  }

  function patchColumn(index: number, patch: Partial<QueryModel["columns"][number]>) {
    setModel(m => ({ ...m, columns: m.columns.map((c, i) => (i === index ? { ...c, ...patch } : c)) }));
  }

  function patchHaving(index: number, patch: Partial<QueryModel["filters"][number]>) {
    setModel(m => ({
      ...m,
      having: (m.having ?? []).map((h, i) => (i === index ? { ...h, ...patch } : h)),
    }));
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
