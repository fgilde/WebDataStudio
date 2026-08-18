import { useMemo, useState } from "react";
import { Badge, Group, ScrollArea, Text } from "@mantine/core";
import { IconChevronDown, IconChevronRight } from "@tabler/icons-react";
import { groupRows } from "./grouping";
import { CellValue } from "./CellValue";

/// Grouping is a reading aid over the rows already fetched, so it renders plainly rather than
/// virtualised — collapsed groups keep the row count on screen small by themselves.
export function GroupedTable({ columns, rows, groupBy }: {
  columns: { name: string; dataType: string; index: number }[];
  rows: unknown[][];
  groupBy: number;
}) {
  const groups = useMemo(() => groupRows(rows, groupBy), [rows, groupBy]);
  const [open, setOpen] = useState<Set<string>>(new Set());

  const toggle = (label: string) => {
    const next = new Set(open);
    if (next.has(label)) next.delete(label); else next.add(label);
    setOpen(next);
  };

  return (
    <ScrollArea style={{ height: "100%" }}>
      <table style={{ borderCollapse: "collapse", width: "max-content", minWidth: "100%" }}>
        <thead style={{ position: "sticky", top: 0, background: "var(--mantine-color-default)" }}>
          <tr>
            {columns.map(c => (
              <th key={c.index} style={{
                textAlign: "left", padding: "2px 8px", whiteSpace: "nowrap",
                borderBottom: "1px solid var(--mantine-color-default-border)",
              }}>
                <Text size="xs" fw={600}>{c.name}</Text>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {groups.map(group => (
            <>
              <tr key={group.label} onClick={() => toggle(group.label)}
                style={{ cursor: "pointer", background: "var(--mantine-color-default-hover)" }}>
                <td colSpan={columns.length} style={{ padding: "2px 8px" }}>
                  <Group gap={6}>
                    {open.has(group.label) ? <IconChevronDown size={13} /> : <IconChevronRight size={13} />}
                    <Text size="xs" fw={600}>{group.label}</Text>
                    <Badge size="xs" variant="light">{group.count}</Badge>
                    {Object.entries(group.subtotals).map(([index, sum]) => (
                      <Text key={index} size="10px" c="dimmed">
                        Σ {columns.find(c => c.index === Number(index))?.name ?? index}:{" "}
                        {Math.round(sum * 1000) / 1000}
                      </Text>
                    ))}
                  </Group>
                </td>
              </tr>

              {open.has(group.label) && group.rows.map((row, i) => (
                <tr key={`${group.label}-${i}`}>
                  {columns.map(c => (
                    <td key={c.index} style={{
                      padding: "2px 8px", whiteSpace: "nowrap",
                      borderBottom: "1px solid var(--mantine-color-default-border)",
                    }}>
                      <CellValue value={row[c.index]} />
                    </td>
                  ))}
                </tr>
              ))}
            </>
          ))}
        </tbody>
      </table>
    </ScrollArea>
  );
}
