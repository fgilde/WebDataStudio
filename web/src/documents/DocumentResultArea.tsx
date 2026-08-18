import { useState } from "react";
import { Badge, Group, ScrollArea, SegmentedControl, Table, Text, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronRight } from "@tabler/icons-react";
import { flattenDocuments, isFlat } from "./flattenDocuments";
import { CellValue } from "../grid/CellValue";

/// Documents render as a JSON tree by default and as a table when they are all flat, which is what
/// makes a Mongo result readable without pretending it is relational.
export function DocumentResultArea({ documents, elapsedMs }: {
  documents: unknown[];
  elapsedMs: number | null;
}) {
  const flat = isFlat(documents);
  const [view, setView] = useState<"tree" | "table">(flat ? "table" : "tree");

  if (documents.length === 0)
    return <Text size="xs" c="dimmed" p="xs">No documents.</Text>;

  const table = flattenDocuments(documents);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4}>
        <SegmentedControl size="xs" value={view} onChange={v => setView(v as "tree" | "table")}
          data={[
            { label: "Tree", value: "tree" },
            { label: "Table", value: "table", disabled: !flat },
          ]} />
        <Text size="xs" c="dimmed">
          {documents.length} documents{elapsedMs !== null && ` · ${elapsedMs} ms`}
          {!flat && " · nested, table view unavailable"}
        </Text>
      </Group>

      <ScrollArea style={{ flex: 1 }}>
        {view === "tree"
          ? documents.map((document, i) => (
              <JsonTreeView key={i} label={`[${i}]`} value={document} depth={0} defaultOpen={i < 3} />
            ))
          : (
            <Table fz="xs" striped withTableBorder>
              <Table.Thead>
                <Table.Tr>{table.columns.map(c => <Table.Th key={c}>{c}</Table.Th>)}</Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {table.rows.map((row, i) => (
                  <Table.Tr key={i}>
                    {row.map((value, j) => <Table.Td key={j}><CellValue value={value} /></Table.Td>)}
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
      </ScrollArea>
    </div>
  );
}

export function JsonTreeView({ label, value, depth, defaultOpen = false }: {
  label: string;
  value: unknown;
  depth: number;
  defaultOpen?: boolean;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const isObject = typeof value === "object" && value !== null;

  if (!isObject)
    return (
      <Group gap={6} wrap="nowrap" style={{ paddingLeft: depth * 14 + 18 }}>
        <Text size="xs" c="dimmed">{label}</Text>
        <CellValue value={value} />
      </Group>
    );

  const entries = Array.isArray(value)
    ? value.map((v, i) => [String(i), v] as const)
    : Object.entries(value as Record<string, unknown>);

  return (
    <div>
      <UnstyledButton onClick={() => setOpen(o => !o)} style={{ paddingLeft: depth * 14 + 4 }}>
        <Group gap={4} wrap="nowrap">
          {open ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
          <Text size="xs" fw={600}>{label}</Text>
          <Badge size="xs" variant="light">
            {Array.isArray(value) ? `${entries.length} items` : `${entries.length} fields`}
          </Badge>
        </Group>
      </UnstyledButton>

      {open && entries.map(([key, child]) => (
        <JsonTreeView key={key} label={key} value={child} depth={depth + 1} />
      ))}
    </div>
  );
}
