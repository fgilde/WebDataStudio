import { Alert, Group, ScrollArea, Select, Table, Text } from "@mantine/core";
import { useMemo, useState } from "react";
import { pivot, MAX_COLUMNS, type PivotAggregate } from "./pivot";
import type { QueryColumn } from "../query/runQuery";

const AGGREGATES: { value: PivotAggregate; label: string }[] = [
  { value: "count", label: "how many" },
  { value: "sum", label: "sum of" },
  { value: "avg", label: "average of" },
  { value: "min", label: "smallest" },
  { value: "max", label: "largest" },
];

/// A result crossed: one column down the side, another across the top.
///
/// The grid answers "what is in here" and grouping answers "how many per status". This is the third
/// question — "how many per status per month" — and it is answered over the rows already on screen
/// rather than by sending a GROUP BY somebody has to write first.
export function PivotView({ columns, rows }: { columns: QueryColumn[]; rows: unknown[][] }) {
  const names = columns.map(column => column.name);

  const [row, setRow] = useState(names[0] ?? "");
  const [column, setColumn] = useState(names[1] ?? "");
  const [value, setValue] = useState(names.find((_, index) => index > 1) ?? names[0] ?? "");
  const [aggregate, setAggregate] = useState<PivotAggregate>("count");

  const result = useMemo(
    () => pivot(columns, rows, { row, column, value, aggregate }),
    [columns, rows, row, column, value, aggregate]);

  const show = (number: number | null) =>
    number === null ? "" : Number.isInteger(number) ? String(number) : number.toFixed(2);

  const options = names.map(name => ({ value: name, label: name }));

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4} wrap="nowrap">
        <Select size="xs" w={130} aria-label="Rows" data={options} value={row}
          onChange={picked => setRow(picked ?? "")} />
        <Text size="xs" c="dimmed">by</Text>
        <Select size="xs" w={130} aria-label="Columns" data={options} value={column} clearable
          onChange={picked => setColumn(picked ?? "")} />
        <Text size="xs" c="dimmed">·</Text>
        <Select size="xs" w={110} aria-label="Aggregate"
          data={AGGREGATES.map(one => ({ value: one.value, label: one.label }))}
          value={aggregate} onChange={picked => setAggregate((picked ?? "count") as PivotAggregate)} />
        {aggregate !== "count" && (
          <Select size="xs" w={130} aria-label="Value" data={options} value={value}
            onChange={picked => setValue(picked ?? "")} />
        )}
      </Group>

      {result.truncated && (
        <Alert color="yellow" p={6} mx={4} mb={4}>
          <Text size="xs">
            {column} has more than {MAX_COLUMNS} different values; the rest are left out. Filter the
            result first, or put it down the side instead.
          </Text>
        </Alert>
      )}

      <ScrollArea style={{ flex: 1, minHeight: 0 }}>
        <Table fz="xs" striped highlightOnHover withColumnBorders stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{row}</Table.Th>
              {result.columns.map(name => (
                <Table.Th key={name} style={{ textAlign: "right" }}>{name || "all"}</Table.Th>
              ))}
              <Table.Th style={{ textAlign: "right" }}>all</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {result.rows.map(one => (
              <Table.Tr key={one.key}>
                <Table.Td>{one.key}</Table.Td>
                {one.cells.map((cell, index) => (
                  <Table.Td key={index} style={{ textAlign: "right" }}>{show(cell)}</Table.Td>
                ))}
                <Table.Td style={{ textAlign: "right", fontWeight: 600 }}>{show(one.total)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
          <Table.Tfoot>
            <Table.Tr>
              <Table.Th>all</Table.Th>
              {result.totals.map((cell, index) => (
                <Table.Th key={index} style={{ textAlign: "right" }}>{show(cell)}</Table.Th>
              ))}
              <Table.Th style={{ textAlign: "right" }}>{show(result.grand)}</Table.Th>
            </Table.Tr>
          </Table.Tfoot>
        </Table>
      </ScrollArea>
    </div>
  );
}
