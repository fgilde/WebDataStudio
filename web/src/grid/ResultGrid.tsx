import { useEffect, useMemo, useRef, useState } from "react";
import { Group, Menu, Text, TextInput } from "@mantine/core";
import { IconFilter, IconSortAscending, IconSortDescending } from "@tabler/icons-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { StatementResult } from "../query/resultStore";
import { CellValue } from "./CellValue";
import { CellViewerModal, type CellRef } from "./CellViewerModal";
import { summarizeSelection } from "./aggregate";
import { GroupedTable } from "./GroupedTable";

const ROW_HEIGHT = 24;

export function ResultGrid({ result, onSelectionChange }: {
  result: StatementResult;
  onSelectionChange?: (values: unknown[]) => void;
}) {
  const parentRef = useRef<HTMLDivElement>(null);
  const [sort, setSort] = useState<{ index: number; desc: boolean } | null>(null);
  const [filters, setFilters] = useState<Record<number, string>>({});
  const [search, setSearch] = useState("");
  const [hidden, setHidden] = useState<Set<number>>(new Set());
  const [selected, setSelected] = useState<{ row: number; col: number }[]>([]);
  const [viewing, setViewing] = useState<CellRef | null>(null);
  const [groupBy, setGroupBy] = useState<number | null>(null);

  const visibleColumns = result.columns
    .map((c, index) => ({ ...c, index }))
    .filter(c => !hidden.has(c.index));

  // Sorting and filtering happen client-side over the rows already fetched. Anything beyond the
  // fetch cap needs a server round trip, which the result footer's "load more" will trigger.
  const rows = useMemo(() => {
    let out = result.rows;

    const active = Object.entries(filters).filter(([, v]) => v.trim() !== "");
    if (active.length > 0)
      out = out.filter(row => active.every(([i, v]) =>
        String(row[Number(i)] ?? "").toLowerCase().includes(v.toLowerCase())));

    if (search.trim() !== "")
      out = out.filter(row => row.some(cell =>
        String(cell ?? "").toLowerCase().includes(search.toLowerCase())));

    if (sort) {
      const { index, desc } = sort;
      out = out.slice().sort((a, b) => {
        const x = a[index];
        const y = b[index];
        if (x === null || x === undefined) return desc ? 1 : -1;
        if (y === null || y === undefined) return desc ? -1 : 1;
        const nx = Number(x);
        const ny = Number(y);
        const cmp = Number.isFinite(nx) && Number.isFinite(ny)
          ? nx - ny
          : String(x).localeCompare(String(y));
        return desc ? -cmp : cmp;
      });
    }
    return out;
  }, [result.rows, filters, search, sort]);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 20,
  });

  const selectedValues = selected.map(s => rows[s.row]?.[s.col]);
  const summary = summarizeSelection(selectedValues);

  useEffect(() => { onSelectionChange?.(selectedValues); },
    // The array identity changes every render; its contents are what matter.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [JSON.stringify(selectedValues), onSelectionChange]);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4} wrap="nowrap">
        <TextInput size="xs" flex={1} placeholder="Search in result"
          value={search} onChange={e => setSearch(e.currentTarget.value)} />
        <Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>
          {rows.length === result.rows.length
            ? `${rows.length} rows`
            : `${rows.length} of ${result.rows.length} rows`}
          {result.truncated && " · capped"}
          {result.elapsedMs !== null && ` · ${result.elapsedMs} ms`}
        </Text>
      </Group>

      {groupBy !== null ? (
        <div style={{ flex: 1, minHeight: 0 }}>
          <GroupedTable columns={visibleColumns} rows={rows} groupBy={groupBy} />
        </div>
      ) : (
      <div ref={parentRef} style={{ flex: 1, overflow: "auto", minHeight: 0 }}>
        <table style={{ borderCollapse: "collapse", width: "max-content", minWidth: "100%" }}>
          <thead style={{ position: "sticky", top: 0, zIndex: 1, background: "var(--mantine-color-default)" }}>
            <tr>
              {visibleColumns.map(c => (
                <th key={c.index} style={{
                  textAlign: "left", padding: "2px 8px", whiteSpace: "nowrap",
                  borderBottom: "1px solid var(--mantine-color-default-border)",
                }}>
                  <Menu withinPortal closeOnItemClick={false}>
                    <Menu.Target>
                      <Group gap={2} style={{ cursor: "pointer" }} wrap="nowrap">
                        <Text size="xs" fw={600}>{c.name}</Text>
                        {sort?.index === c.index && (sort.desc
                          ? <IconSortDescending size={12} /> : <IconSortAscending size={12} />)}
                        {filters[c.index] && <IconFilter size={12} />}
                      </Group>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item onClick={() => setSort({ index: c.index, desc: false })}>Sort ascending</Menu.Item>
                      <Menu.Item onClick={() => setSort({ index: c.index, desc: true })}>Sort descending</Menu.Item>
                      <Menu.Item onClick={() => setSort(null)}>Clear sort</Menu.Item>
                      <Menu.Divider />
                      <Menu.Item>
                        <TextInput size="xs" placeholder="Filter" value={filters[c.index] ?? ""}
                          onChange={e => setFilters(f => ({ ...f, [c.index]: e.currentTarget.value }))} />
                      </Menu.Item>
                      <Menu.Divider />
                      <Menu.Item onClick={() => setGroupBy(c.index)}>Group by this column</Menu.Item>
                      <Menu.Item disabled={groupBy === null}
                        onClick={() => setGroupBy(null)}>Clear grouping</Menu.Item>
                      <Menu.Divider />
                      <Menu.Item onClick={() => setHidden(h => new Set(h).add(c.index))}>Hide column</Menu.Item>
                      <Menu.Item onClick={() => setHidden(new Set())}>Show all columns</Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                  <Text size="10px" c="dimmed">{c.dataType}</Text>
                </th>
              ))}
            </tr>
          </thead>
          <tbody style={{ position: "relative", height: virtualizer.getTotalSize(), display: "block" }}>
            {virtualizer.getVirtualItems().map(item => (
              <tr key={item.key} style={{
                position: "absolute", top: 0, left: 0, display: "table", width: "100%",
                tableLayout: "fixed", height: ROW_HEIGHT, transform: `translateY(${item.start}px)`,
              }}>
                {visibleColumns.map(c => {
                  const isSelected = selected.some(s => s.row === item.index && s.col === c.index);
                  return (
                    <td key={c.index}
                      onMouseDown={e => setSelected(prev => e.ctrlKey || e.metaKey
                        ? [...prev, { row: item.index, col: c.index }]
                        : [{ row: item.index, col: c.index }])}
                      onDoubleClick={() => setViewing({
                        row: item.index, col: c.index, column: c.name, value: rows[item.index]?.[c.index],
                      })}
                      style={{
                        padding: "2px 8px", whiteSpace: "nowrap", cursor: "cell", overflow: "hidden",
                        textOverflow: "ellipsis",
                        borderBottom: "1px solid var(--mantine-color-default-border)",
                        background: isSelected ? "var(--mantine-primary-color-light)" : undefined,
                      }}>
                      <CellValue value={rows[item.index]?.[c.index]} />
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      )}

      {selected.length > 0 && (
        <Group gap={12} px={8} py={2} style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}>
          <Text size="xs" c="dimmed">Selected: {summary.count}</Text>
          {summary.sum !== null && <Text size="xs" c="dimmed">Sum: {summary.sum}</Text>}
          {summary.avg !== null && <Text size="xs" c="dimmed">Avg: {summary.avg.toFixed(4)}</Text>}
          {summary.min !== null && <Text size="xs" c="dimmed">Min: {summary.min}</Text>}
          {summary.max !== null && <Text size="xs" c="dimmed">Max: {summary.max}</Text>}
        </Group>
      )}

      <CellViewerModal cell={viewing} onClose={() => setViewing(null)} />
    </div>
  );
}
