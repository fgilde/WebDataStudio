import { useEffect, useMemo, useState } from "react";
import {
  ActionIcon, Alert, Badge, Group, Loader, ScrollArea, Select, Stack, Text, Tooltip,
} from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconRefresh } from "@tabler/icons-react";
import { browseData, loadDiagram, type DataPageDto, type DiagramDto } from "../api";
import { filterForValue, refOfTable, relationsOf, type Relation } from "./relations";

/// How many rows one level shows. A perspective is for following a shape, not for paging through a
/// table — that is what the data tab is for.
const PAGE = 25;

/// A nested table view over related data: a row, the rows it points at, the rows that point back at
/// it, as deep as you care to open. DbGate calls this a perspective; the studio already had the
/// foreign-key graph the ER diagram draws, and this walks it one row at a time.
///
/// Only single-column keys are followed: a composite key cannot be followed by comparing one value,
/// and showing the wrong rows would be worse than not offering it.
export function PerspectivePanel({ connectionId, initialTable }: {
  connectionId: string;
  initialTable?: string;
}) {
  const [diagram, setDiagram] = useState<DiagramDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [table, setTable] = useState<string | null>(initialTable ?? null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    let cancelled = false;

    loadDiagram(connectionId)
      .then(found => {
        if (cancelled) return;
        setDiagram(found);
        setError(null);
        setTable(current => current ?? found.nodes[0]?.id ?? null);
      })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; };
  }, [connectionId, nonce]);

  const tables = useMemo(() => (diagram?.nodes ?? []).map(node => node.id).sort(), [diagram]);

  if (error) return <Alert color="red" m="xs"><Text size="xs">{error}</Text></Alert>;
  if (!diagram) return <Loader size="xs" m="xs" />;

  return (
    <Stack gap={4} h="100%" p="xs" style={{ minHeight: 0 }}>
      <Group gap="xs">
        <Select size="xs" w={260} searchable label={undefined} placeholder="Start from"
          data={tables} value={table} onChange={setTable} allowDeselect={false} />
        <Tooltip label="Re-read the schema and reload">
          <ActionIcon size="sm" variant="subtle" aria-label="Reload"
            onClick={() => setNonce(n => n + 1)}>
            <IconRefresh size={14} />
          </ActionIcon>
        </Tooltip>
        <Text size="10px" c="dimmed">
          Open a row to see what it points at, and what points back at it.
        </Text>
      </Group>

      <ScrollArea style={{ flex: 1 }}>
        {table
          ? <Level key={`${table}:${nonce}`} connectionId={connectionId} diagram={diagram}
              table={table} depth={0} />
          : <Text size="xs" c="dimmed">This schema has no tables.</Text>}
      </ScrollArea>
    </Stack>
  );
}

/// One table's rows at one level of the tree. `filter` is what selects them; the root level has
/// none.
function Level({ connectionId, diagram, table, depth, filterColumn, filter }: {
  connectionId: string;
  diagram: DiagramDto;
  table: string;
  depth: number;
  filterColumn?: string;
  filter?: string;
}) {
  const [page, setPage] = useState<DataPageDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<number | null>(null);

  useEffect(() => {
    let cancelled = false;

    browseData(connectionId, refOfTable(table), { limit: PAGE, filterColumn, filter })
      .then(found => { if (!cancelled) { setPage(found); setError(null); } })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });

    return () => { cancelled = true; setPage(null); };
  }, [connectionId, table, filterColumn, filter]);

  if (error) return <Text size="10px" c="red" pl={depth * 12}>{error}</Text>;
  if (!page) return <Loader size="xs" ml={depth * 12} my={2} />;

  if (page.rows.length === 0)
    return <Text size="10px" c="dimmed" pl={depth * 12 + 18}>no rows</Text>;

  const relations = relationsOf(diagram, table);

  return (
    <Stack gap={0}>
      {page.rows.map((row, index) => (
        <div key={index}>
          <Group gap={4} wrap="nowrap" pl={depth * 12}
            style={{ cursor: "pointer", borderBottom: "1px solid var(--mantine-color-default-border)" }}
            onClick={() => setOpen(current => (current === index ? null : index))}>
            {relations.length > 0
              ? (open === index ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />)
              : <span style={{ width: 12 }} />}

            {/* The row itself, as its columns. Wide tables are cut off rather than wrapped: this is
                a shape to follow, and the data tab is where a row is read in full. */}
            <Text size="10px" ff="monospace" lineClamp={1}>
              {page.columns.slice(0, 8).map((column, at) =>
                `${column.name}=${row[at] === null ? "∅" : String(row[at])}`).join("  ")}
            </Text>
          </Group>

          {open === index && (
            <Stack gap={0} pl={depth * 12 + 14} py={2}>
              {relations.length === 0 && (
                <Text size="10px" c="dimmed">Nothing is related to this table by a single column.</Text>
              )}
              {relations.map(relation => (
                <Branch key={`${relation.direction}:${relation.table}:${relation.from}:${relation.to}`}
                  connectionId={connectionId} diagram={diagram} depth={depth + 1}
                  relation={relation} value={valueOf(page, row, relation)} />
              ))}
            </Stack>
          )}
        </div>
      ))}

      {page.rows.length === PAGE && (
        <Text size="9px" c="dimmed" pl={depth * 12 + 18}>
          the first {PAGE}; the data tab pages through the rest
        </Text>
      )}
    </Stack>
  );
}

/// The value on this side of a relation, or undefined when the column is not in the page (a masked
/// or hidden column, or a key the projection left out).
function valueOf(page: DataPageDto, row: unknown[], relation: Relation): unknown {
  const index = page.columns.findIndex(column =>
    column.name.toLowerCase() === relation.from.toLowerCase());

  return index < 0 ? undefined : row[index];
}

/// One relation under an opened row: collapsed until asked for, because every one of them is a
/// query.
function Branch({ connectionId, diagram, depth, relation, value }: {
  connectionId: string;
  diagram: DiagramDto;
  depth: number;
  relation: Relation;
  value: unknown;
}) {
  const [open, setOpen] = useState(false);

  if (value === undefined)
    return (
      <Text size="10px" c="dimmed">
        {relation.label} — {relation.from} is not in this page
      </Text>
    );

  return (
    <div>
      <Group gap={4} wrap="nowrap" style={{ cursor: "pointer" }} onClick={() => setOpen(o => !o)}>
        {open ? <IconChevronDown size={11} /> : <IconChevronRight size={11} />}
        <Badge size="xs" variant="light" color={relation.direction === "out" ? "blue" : "teal"}>
          {relation.direction === "out" ? "→" : "←"}
        </Badge>
        <Text size="10px">{relation.label}</Text>
      </Group>

      {open && (
        <Level connectionId={connectionId} diagram={diagram} table={relation.table} depth={depth}
          filterColumn={relation.to} filter={filterForValue(value)} />
      )}
    </div>
  );
}
